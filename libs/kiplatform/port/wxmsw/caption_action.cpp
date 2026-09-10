/* Copyright The KiCad Developers, see AUTHORS.txt for contributors.
 * SPDX-License-Identifier: GPL-2.0-or-later
 */
#include "caption_action.h"
#include <commctrl.h>
#include <dwmapi.h>
#include <oleacc.h>
#include <windowsx.h>
#include <algorithm>
#include <atomic>
#include <memory>
#include <stdexcept>

namespace
{
constexpr wchar_t PROPERTY[] = L"KICAD_CAPTION_ACTION_V1";

class CAPTION_FONT
{
public:
    CAPTION_FONT( HWND window, HDC dc ) : m_dc( dc )
    {
        NONCLIENTMETRICSW metrics{}; metrics.cbSize = sizeof( metrics );
        if( SystemParametersInfoForDpi( SPI_GETNONCLIENTMETRICS, sizeof( metrics ), &metrics, 0, GetDpiForWindow( window ) ) )
            m_font = CreateFontIndirectW( &metrics.lfCaptionFont );
        m_previous = SelectObject( dc, m_font ? m_font : GetStockObject( DEFAULT_GUI_FONT ) );
    }
    ~CAPTION_FONT()
    {
        SelectObject( m_dc, m_previous );
        if( m_font ) DeleteObject( m_font );
    }
private:
    HDC m_dc;
    HFONT m_font = nullptr;
    HGDIOBJ m_previous = nullptr;
};

struct ACTION
{
    HWND window;
    std::wstring label;
    std::function<void()> invoke;
    bool visible = false, enabled = false, pressed = false;
    RECT button = {}, title = {};
    HMENU menu = nullptr;
    UINT menuId = 0;

    bool Shown() const { return window && visible && IsWindowVisible( window ) && !IsIconic( window ); }
    bool Ready() const { return Shown() && enabled && IsWindowEnabled( window ) && !IsRectEmpty( &button ); }
    void Invoke() { if( Ready() ) { auto callback = invoke; callback(); } }

    void Layout()
    {
        button = {}; title = {};
        if( !window ) return;
        TITLEBARINFOEX info = {}; info.cbSize = sizeof( info );
        SendMessageW( window, WM_GETTITLEBARINFOEX, 0, reinterpret_cast<LPARAM>( &info ) );
        title = info.rcTitleBar;
        if( title.bottom <= title.top || title.right <= title.left ) return;
        LONG right = title.right;
        for( int i = 2; i < CCHILDREN_TITLEBAR + 1; ++i )
            if( !( info.rgstate[i] & STATE_SYSTEM_INVISIBLE ) && info.rgrect[i].right > info.rgrect[i].left )
                right = std::min( right, info.rgrect[i].left );
        const int height = title.bottom - title.top;
        HDC dc = GetWindowDC( window );
        SIZE text = {};
        if( dc )
        {
            {
                CAPTION_FONT font( window, dc );
                GetTextExtentPoint32W( dc, label.data(), static_cast<int>( label.size() ), &text );
            }
            ReleaseDC( window, dc );
        }
        const int gap = std::max( 2, height / 10 );
        const int width = std::max( height * 2, static_cast<int>( text.cx ) + height );
        if( right - title.left < width + height * 2 ) return;
        button = { right - width - gap, title.top + gap, right - gap, title.bottom - gap };
        title.right = button.left - gap;
    }

    void Paint()
    {
        if( !Shown() ) return;
        Layout();
        if( IsRectEmpty( &button ) ) return;
        RECT outer; if( !GetWindowRect( window, &outer ) ) return;
        RECT caption = title, control = button;
        // Windows' title-bar accessibility rectangle can begin after the icon
        // or the first title glyph. Repaint the complete caption strip from the
        // frame inset, otherwise a prefix of the old title remains underneath.
        caption.left = outer.left + GetSystemMetricsForDpi( SM_CXFRAME, GetDpiForWindow( window ) );
        OffsetRect( &caption, -outer.left, -outer.top ); OffsetRect( &control, -outer.left, -outer.top );
        HDC dc = GetWindowDC( window ); if( !dc ) return;
        // Retain native non-client sizing, system buttons, menus, dragging and
        // resizing. Only this caption strip is repainted to reserve title space.
        FillRect( dc, &caption, GetSysColorBrush( GetForegroundWindow() == window ? COLOR_ACTIVECAPTION : COLOR_INACTIVECAPTION ) );
        UINT flags = DC_TEXT | DC_ICON | DC_GRADIENT;
        if( GetForegroundWindow() == window ) flags |= DC_ACTIVE;
        DrawCaption( window, dc, &caption, flags );
        DrawFrameControl( dc, &control, DFC_BUTTON,
                DFCS_BUTTONPUSH | ( Ready() ? 0 : DFCS_INACTIVE ) | ( pressed ? DFCS_PUSHED : 0 ) );
        {
            CAPTION_FONT font( window, dc );
            const int background = SetBkMode( dc, TRANSPARENT );
            const auto color = SetTextColor( dc, GetSysColor( Ready() ? COLOR_BTNTEXT : COLOR_GRAYTEXT ) );
            DrawTextW( dc, label.data(), static_cast<int>( label.size() ), &control,
                    DT_SINGLELINE | DT_CENTER | DT_VCENTER | DT_NOPREFIX );
            SetTextColor( dc, color ); SetBkMode( dc, background );
        }
        ReleaseDC( window, dc );
    }

    void Menu()
    {
        if( !window ) return;
        auto current = GetSystemMenu( window, FALSE );
        if( current != menu ) { menu = current; menuId = 0; }
        if( !menu ) return;
        if( !visible )
        {
            if( menuId ) DeleteMenu( menu, menuId, MF_BYCOMMAND );
            menuId = 0; return;
        }
        if( !menuId )
        {
            for( UINT id = 0x1000; id < 0xf000; id += 0x10 )
                if( GetMenuState( menu, id, MF_BYCOMMAND ) == static_cast<UINT>( -1 ) )
                {
                    if( AppendMenuW( menu, MF_STRING | MF_GRAYED, id, label.c_str() ) ) menuId = id;
                    break;
                }
        }
        if( menuId ) EnableMenuItem( menu, menuId, MF_BYCOMMAND | ( Ready() ? MF_ENABLED : MF_GRAYED ) );
    }
};

// Append one simple accessible child to the real title bar; preserve all native
// system-button accessibility by delegating their properties to Windows.
class ACCESSIBLE final : public IAccessible
{
public:
    ACCESSIBLE( std::weak_ptr<ACTION> action, IAccessible* native, long count ) :
            m_action( std::move( action ) ), m_native( native ), m_child( count + 1 ) {}
    ~ACCESSIBLE() { m_native->Release(); }
    HRESULT STDMETHODCALLTYPE QueryInterface( REFIID id, void** value ) override
    {
        if( !value ) return E_POINTER;
        *value = nullptr;
        if( id != IID_IUnknown && id != IID_IDispatch && id != IID_IAccessible ) return E_NOINTERFACE;
        *value = static_cast<IAccessible*>( this ); AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++m_refs; }
    ULONG STDMETHODCALLTYPE Release() override { auto refs = --m_refs; if( !refs ) delete this; return refs; }
    HRESULT STDMETHODCALLTYPE GetTypeInfoCount( UINT* count ) override { return m_native->GetTypeInfoCount( count ); }
    HRESULT STDMETHODCALLTYPE GetTypeInfo( UINT index, LCID locale, ITypeInfo** value ) override { return m_native->GetTypeInfo( index, locale, value ); }
    HRESULT STDMETHODCALLTYPE GetIDsOfNames( REFIID id, LPOLESTR* names, UINT count, LCID locale, DISPID* ids ) override
    { return m_native->GetIDsOfNames( id, names, count, locale, ids ); }
    HRESULT STDMETHODCALLTYPE Invoke( DISPID id, REFIID, LCID locale, WORD flags, DISPPARAMS* args,
            VARIANT* result, EXCEPINFO* exception, UINT* argumentError ) override
    {
        ITypeInfo* type = nullptr; auto hr = GetTypeInfo( 0, locale, &type );
        if( FAILED( hr ) ) return hr;
        hr = DispInvoke( static_cast<IAccessible*>( this ), type, id, flags, args, result, exception, argumentError );
        type->Release(); return hr;
    }
    HRESULT STDMETHODCALLTYPE get_accParent( IDispatch** value ) override { return m_native->get_accParent( value ); }
    HRESULT STDMETHODCALLTYPE get_accChildCount( long* value ) override { if( !value ) return E_POINTER; *value = m_child; return S_OK; }
    HRESULT STDMETHODCALLTYPE get_accChild( VARIANT child, IDispatch** value ) override
    { if( !Mine( child ) ) return m_native->get_accChild( child, value ); if( !value ) return E_POINTER; *value = nullptr; return S_FALSE; }
    HRESULT STDMETHODCALLTYPE get_accName( VARIANT child, BSTR* value ) override
    { if( !Mine( child ) ) return m_native->get_accName( child, value ); return Label( value ); }
    HRESULT STDMETHODCALLTYPE get_accValue( VARIANT child, BSTR* value ) override
    { return Mine( child ) ? Empty( value ) : m_native->get_accValue( child, value ); }
    HRESULT STDMETHODCALLTYPE get_accDescription( VARIANT child, BSTR* value ) override
    { return Mine( child ) ? Empty( value ) : m_native->get_accDescription( child, value ); }
    HRESULT STDMETHODCALLTYPE get_accRole( VARIANT child, VARIANT* value ) override
    {
        if( !Mine( child ) ) return m_native->get_accRole( child, value );
        if( !value ) return E_POINTER; VariantInit( value ); value->vt = VT_I4; value->lVal = ROLE_SYSTEM_PUSHBUTTON; return S_OK;
    }
    HRESULT STDMETHODCALLTYPE get_accState( VARIANT child, VARIANT* value ) override
    {
        if( !Mine( child ) ) return m_native->get_accState( child, value );
        if( !value ) return E_POINTER; VariantInit( value ); value->vt = VT_I4;
        auto action = m_action.lock();
        if( !action || !action->Shown() || IsRectEmpty( &action->button ) ) value->lVal |= STATE_SYSTEM_INVISIBLE;
        if( !action || !action->Ready() ) value->lVal |= STATE_SYSTEM_UNAVAILABLE;
        if( action && action->pressed ) value->lVal |= STATE_SYSTEM_PRESSED;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE get_accHelp( VARIANT child, BSTR* value ) override
    { return Mine( child ) ? Empty( value ) : m_native->get_accHelp( child, value ); }
    HRESULT STDMETHODCALLTYPE get_accHelpTopic( BSTR* value, VARIANT child, long* topic ) override
    { if( !Mine( child ) ) return m_native->get_accHelpTopic( value, child, topic ); if( topic ) *topic = 0; return Empty( value ); }
    HRESULT STDMETHODCALLTYPE get_accKeyboardShortcut( VARIANT child, BSTR* value ) override
    { return Mine( child ) ? Empty( value ) : m_native->get_accKeyboardShortcut( child, value ); }
    HRESULT STDMETHODCALLTYPE get_accFocus( VARIANT* value ) override { return m_native->get_accFocus( value ); }
    HRESULT STDMETHODCALLTYPE get_accSelection( VARIANT* value ) override { return m_native->get_accSelection( value ); }
    HRESULT STDMETHODCALLTYPE get_accDefaultAction( VARIANT child, BSTR* value ) override
    { return Mine( child ) ? Label( value ) : m_native->get_accDefaultAction( child, value ); }
    HRESULT STDMETHODCALLTYPE accSelect( long flags, VARIANT child ) override
    { return Mine( child ) ? S_FALSE : m_native->accSelect( flags, child ); }
    HRESULT STDMETHODCALLTYPE accLocation( long* x, long* y, long* width, long* height, VARIANT child ) override
    {
        if( !Mine( child ) ) return m_native->accLocation( x, y, width, height, child );
        if( !x || !y || !width || !height ) return E_POINTER;
        auto action = m_action.lock(); if( !action || !action->window ) return CO_E_OBJNOTCONNECTED;
        action->Layout(); auto rect = action->button;
        *x = rect.left; *y = rect.top; *width = rect.right - rect.left; *height = rect.bottom - rect.top; return S_OK;
    }
    HRESULT STDMETHODCALLTYPE accNavigate( long direction, VARIANT start, VARIANT* end ) override
    {
        if( !end ) return E_POINTER;
        if( start.vt == VT_I4 && ( ( start.lVal == CHILDID_SELF && direction == NAVDIR_LASTCHILD )
            || ( start.lVal == m_child - 1 && direction == NAVDIR_NEXT ) ) )
        { VariantInit( end ); end->vt = VT_I4; end->lVal = m_child; return S_OK; }
        if( !Mine( start ) ) return m_native->accNavigate( direction, start, end );
        VariantInit( end );
        if( direction == NAVDIR_PREVIOUS && m_child > 1 ) { end->vt = VT_I4; end->lVal = m_child - 1; return S_OK; }
        return S_FALSE;
    }
    HRESULT STDMETHODCALLTYPE accHitTest( long x, long y, VARIANT* value ) override
    {
        auto action = m_action.lock();
        if( action && action->Shown() ) { action->Layout(); if( PtInRect( &action->button, { x, y } ) )
        { if( !value ) return E_POINTER; VariantInit( value ); value->vt = VT_I4; value->lVal = m_child; return S_OK; } }
        return m_native->accHitTest( x, y, value );
    }
    HRESULT STDMETHODCALLTYPE accDoDefaultAction( VARIANT child ) override
    {
        if( !Mine( child ) ) return m_native->accDoDefaultAction( child );
        auto action = m_action.lock(); if( !action || !action->Ready() ) return E_ACCESSDENIED;
        action->Invoke(); return S_OK;
    }
    HRESULT STDMETHODCALLTYPE put_accName( VARIANT child, BSTR value ) override
    { return Mine( child ) ? E_ACCESSDENIED : m_native->put_accName( child, value ); }
    HRESULT STDMETHODCALLTYPE put_accValue( VARIANT child, BSTR value ) override
    { return Mine( child ) ? E_ACCESSDENIED : m_native->put_accValue( child, value ); }
private:
    bool Mine( VARIANT child ) const { return child.vt == VT_I4 && child.lVal == m_child; }
    static HRESULT Empty( BSTR* value ) { if( !value ) return E_POINTER; *value = nullptr; return S_FALSE; }
    HRESULT Label( BSTR* value )
    {
        if( !value ) return E_POINTER; *value = nullptr;
        auto action = m_action.lock(); if( !action || !action->window ) return CO_E_OBJNOTCONNECTED;
        *value = SysAllocString( action->label.c_str() ); return *value ? S_OK : E_OUTOFMEMORY;
    }
    std::atomic<ULONG> m_refs{ 1 };
    std::weak_ptr<ACTION> m_action;
    IAccessible* m_native;
    long m_child;
};

LRESULT CALLBACK Subclass( HWND window, UINT message, WPARAM wParam, LPARAM lParam, UINT_PTR id, DWORD_PTR data )
{
    auto* holder = reinterpret_cast<std::shared_ptr<ACTION>*>( data );
    auto action = *holder;
    if( message == WM_NCDESTROY )
    {
        action->window = nullptr; action->invoke = {}; action->pressed = false;
        RemovePropW( window, PROPERTY ); RemoveWindowSubclass( window, Subclass, id ); delete holder;
        return DefSubclassProc( window, message, wParam, lParam );
    }
    if( message == WM_GETOBJECT && static_cast<LONG>( lParam ) == OBJID_TITLEBAR )
    {
        IAccessible* native = nullptr;
        if( SUCCEEDED( CreateStdAccessibleObject( window, OBJID_TITLEBAR, IID_IAccessible, reinterpret_cast<void**>( &native ) ) ) )
        {
            long count = 0;
            if( SUCCEEDED( native->get_accChildCount( &count ) ) )
            {
                auto accessible = new ACCESSIBLE( action, native, count );
                auto result = LresultFromObject( IID_IAccessible, wParam, accessible ); accessible->Release(); return result;
            }
            native->Release();
        }
    }
    if( message == WM_NCHITTEST && action->Shown() )
    {
        action->Layout();
        if( PtInRect( &action->button, { GET_X_LPARAM( lParam ), GET_Y_LPARAM( lParam ) } ) ) return HTBORDER;
    }
    if( message == WM_NCLBUTTONDOWN && action->Shown() )
    {
        action->Layout();
        if( PtInRect( &action->button, { GET_X_LPARAM( lParam ), GET_Y_LPARAM( lParam ) } ) )
        {
            if( action->Ready() ) { action->pressed = true; SetCapture( window ); action->Paint(); }
            return 0;
        }
    }
    if( message == WM_LBUTTONUP && action->pressed )
    {
        POINT point{ GET_X_LPARAM( lParam ), GET_Y_LPARAM( lParam ) }; ClientToScreen( window, &point );
        bool inside = PtInRect( &action->button, point ); action->pressed = false;
        ReleaseCapture(); action->Paint(); if( inside ) action->Invoke(); return 0;
    }
    if( message == WM_KEYDOWN && wParam == VK_ESCAPE && action->pressed )
    { action->pressed = false; ReleaseCapture(); action->Paint(); return 0; }
    if( message == WM_CANCELMODE || message == WM_CAPTURECHANGED )
    {
        action->pressed = false;
        if( message == WM_CANCELMODE && GetCapture() == window ) ReleaseCapture();
        action->Paint();
    }
    if( message == WM_SYSCOMMAND && action->menuId && ( wParam & 0xfff0 ) == action->menuId )
    { action->Invoke(); return 0; }
    auto result = DefSubclassProc( window, message, wParam, lParam );
    if( message == WM_NCPAINT || message == WM_NCACTIVATE || message == WM_SETTEXT
        || message == WM_WINDOWPOSCHANGED || message == WM_THEMECHANGED || message == WM_DPICHANGED
        || message == WM_ENABLE || message == WM_SHOWWINDOW ) { action->Layout(); action->Menu(); action->Paint(); }
    return result;
}
}

std::function<void( bool, bool )> KIPLATFORM::UI::AddWindowsCaptionAction( HWND window,
        const std::wstring& label, std::function<void()> callback )
{
    if( !IsWindow( window ) || GetWindowThreadProcessId( window, nullptr ) != GetCurrentThreadId()
        || label.empty() || !callback || GetPropW( window, PROPERTY ) )
        throw std::invalid_argument( "Use a live owning-thread window with one caption action." );
    auto action = std::make_shared<ACTION>(); action->window = window; action->label = label; action->invoke = std::move( callback );
    auto holder = std::make_unique<std::shared_ptr<ACTION>>( action );
    if( !SetPropW( window, PROPERTY, holder.get() ) ) throw std::runtime_error( "Cannot own the caption action." );
    if( !SetWindowSubclass( window, Subclass, 1, reinterpret_cast<DWORD_PTR>( holder.get() ) ) )
    { RemovePropW( window, PROPERTY ); throw std::runtime_error( "Cannot attach the caption action." ); }
    holder.release();
    return [weak = std::weak_ptr<ACTION>( action )]( bool visible, bool enabled )
    {
        auto state = weak.lock(); if( !state || !state->window ) return;
        if( GetWindowThreadProcessId( state->window, nullptr ) != GetCurrentThreadId() )
            throw std::logic_error( "Caption changes require the owning UI thread." );
        bool changed = state->visible != visible;
        state->visible = visible; state->enabled = enabled;
        if( !visible || !enabled ) { state->pressed = false; if( GetCapture() == state->window ) ReleaseCapture(); }
        if( changed )
        {
            DWMNCRENDERINGPOLICY policy = visible ? DWMNCRP_DISABLED : DWMNCRP_USEWINDOWSTYLE;
            DwmSetWindowAttribute( state->window, DWMWA_NCRENDERING_POLICY, &policy, sizeof( policy ) );
            SetWindowPos( state->window, nullptr, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED );
        }
        if( !state->window ) return;
        state->Layout(); state->Menu();
        RedrawWindow( state->window, nullptr, nullptr, RDW_INVALIDATE | RDW_FRAME | RDW_UPDATENOW );
        if( state->window ) { state->Paint(); NotifyWinEvent( EVENT_OBJECT_STATECHANGE, state->window, OBJID_TITLEBAR, CHILDID_SELF ); }
    };
}
