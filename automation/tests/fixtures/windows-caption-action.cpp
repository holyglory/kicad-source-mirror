// Exercise the real native caption component, not a replacement click handler.
#include "caption_action.h"
#include <oleacc.h>
#include <commctrl.h>
#include <nlohmann/json.hpp>
#include <array>
#include <iostream>
#include <memory>
#include <thread>

using json = nlohmann::json;
struct FRAME { HWND window = nullptr; int index = 0, clicks = 0, escapes = 0; std::function<void( bool, bool )> set; };
std::array<FRAME, 2> frames;

std::string Utf8( const std::wstring& value )
{
    int count = WideCharToMultiByte( CP_UTF8, WC_ERR_INVALID_CHARS, value.data(), static_cast<int>( value.size() ), nullptr, 0, nullptr, nullptr );
    std::string result( count, '\0' );
    WideCharToMultiByte( CP_UTF8, WC_ERR_INVALID_CHARS, value.data(), static_cast<int>( value.size() ), result.data(), count, nullptr, nullptr );
    return result;
}

struct ACCESS
{
    IAccessible* value = nullptr; VARIANT child = {};
    explicit ACCESS( HWND window )
    {
        HRESULT hr = AccessibleObjectFromWindow( window, OBJID_TITLEBAR, IID_IAccessible, reinterpret_cast<void**>( &value ) );
        if( FAILED( hr ) || !value ) throw std::runtime_error( "Cannot query the actual caption accessibility object." );
        long count = 0;
        if( FAILED( value->get_accChildCount( &count ) ) ) { value->Release(); value = nullptr; throw std::runtime_error( "Cannot enumerate the title bar." ); }
        child.vt = VT_I4; child.lVal = count;
    }
    ~ACCESS() { if( value ) value->Release(); }
};

json Snapshot()
{
    json result = { { "status", "ok" }, { "frames", json::array() } };
    for( auto& frame : frames )
    {
        json item = { { "index", frame.index }, { "clicks", frame.clicks }, { "escapes", frame.escapes }, { "alive", frame.window != nullptr } };
        if( frame.window )
        {
            ACCESS accessible( frame.window );
            BSTR name = nullptr; VARIANT role = {}, state = {};
            accessible.value->get_accName( accessible.child, &name );
            item["label"] = name ? Utf8( name ) : ""; SysFreeString( name );
            accessible.value->get_accRole( accessible.child, &role );
            accessible.value->get_accState( accessible.child, &state );
            item["role"] = role.vt == VT_I4 ? role.lVal : -1;
            item["state"] = state.vt == VT_I4 ? state.lVal : -1;
            long x = 0, y = 0, width = 0, height = 0;
            if( FAILED( accessible.value->accLocation( &x, &y, &width, &height, accessible.child ) ) )
                throw std::runtime_error( "Caption geometry is unavailable." );
            item["rectangle"] = { { "x", x }, { "y", y }, { "width", width }, { "height", height } };
            wchar_t title[1024]; GetWindowTextW( frame.window, title, 1024 ); item["title"] = Utf8( title );
            auto menu = GetSystemMenu( frame.window, FALSE ); int found = 0;
            for( int i = 0; i < GetMenuItemCount( menu ); ++i )
            {
                wchar_t text[128]; GetMenuStringW( menu, i, text, 128, MF_BYPOSITION );
                if( std::wstring( text ) == L"Update" ) ++found;
            }
            item["menuActions"] = found;
        }
        result["frames"].push_back( item );
    }
    return result;
}

void HandleCommand( const std::string& line )
{
    try
    {
        auto command = json::parse( line ); auto& frame = frames.at( command.value( "index", 0 ) );
        auto op = command.value( "op", "state" ); json extra = json::object();
        if( op == "set" ) frame.set( command.at( "visible" ), command.at( "enabled" ) );
        else if( op == "resize" ) SetWindowPos( frame.window, nullptr, 0, 0, command.at( "width" ), 420, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE );
        else if( op == "show" ) ShowWindow( frame.window, command.at( "visible" ).get<bool>() ? SW_SHOW : SW_HIDE );
        else if( op == "enable" ) EnableWindow( frame.window, command.at( "enabled" ) );
        else if( op == "invoke" ) { ACCESS object( frame.window ); extra["invokeResult"] = object.value->accDoDefaultAction( object.child ); }
        else if( op == "duplicate" )
        {
            try { KIPLATFORM::UI::AddWindowsCaptionAction( frame.window, L"Other", []{} ); extra["duplicateRefused"] = false; }
            catch( const std::invalid_argument& ) { extra["duplicateRefused"] = true; }
        }
        else if( op == "wrong-thread" )
        {
            bool refused = false;
            std::thread wrong( [&] { try { frame.set( true, true ); } catch( const std::logic_error& ) { refused = true; } } );
            wrong.join(); extra["wrongThreadRefused"] = refused;
        }
        else if( op == "destroy" )
        {
            ACCESS retained( frame.window ); DestroyWindow( frame.window ); frame.set( true, true );
            extra["staleInvokeResult"] = retained.value->accDoDefaultAction( retained.child );
        }
        auto response = Snapshot(); response.update( extra ); std::cout << response.dump() << '\n' << std::flush;
    }
    catch( const std::exception& error ) { std::cout << json( { { "status", "failed" }, { "error", error.what() } } ).dump() << '\n' << std::flush; }
}

LRESULT CALLBACK WindowProc( HWND window, UINT message, WPARAM wParam, LPARAM lParam )
{
    if( message == WM_APP )
    {
        std::unique_ptr<std::string> line( reinterpret_cast<std::string*>( lParam ) );
        HandleCommand( *line ); return 0;
    }
    if( message == WM_APP + 1 ) { PostQuitMessage( 0 ); return 0; }
    auto* frame = reinterpret_cast<FRAME*>( GetWindowLongPtrW( window, GWLP_USERDATA ) );
    if( message == WM_NCCREATE )
    {
        frame = static_cast<FRAME*>( reinterpret_cast<CREATESTRUCTW*>( lParam )->lpCreateParams );
        SetWindowLongPtrW( window, GWLP_USERDATA, reinterpret_cast<LONG_PTR>( frame ) );
    }
    if( message == WM_KEYUP && wParam == VK_ESCAPE && frame ) ++frame->escapes;
    if( message == WM_PAINT || message == WM_PRINTCLIENT )
    {
        PAINTSTRUCT paint; HDC dc = message == WM_PAINT ? BeginPaint( window, &paint ) : reinterpret_cast<HDC>( wParam );
        RECT client; GetClientRect( window, &client ); FillRect( dc, &client, reinterpret_cast<HBRUSH>( COLOR_WINDOW + 1 ) );
        SetBkMode( dc, TRANSPARENT );
        const wchar_t* text = L"Native caption fixture, not a KiCad design";
        TextOutW( dc, 32, 50, text, lstrlenW( text ) );
        RECT block = { 32, 90, 220, 150 }; auto brush = CreateSolidBrush( RGB( 35, 110, 175 ) );
        FillRect( dc, &block, brush ); DeleteObject( brush );
        if( message == WM_PAINT ) EndPaint( window, &paint ); return 0;
    }
    if( message == WM_DESTROY && frame ) frame->window = nullptr;
    return DefWindowProcW( window, message, wParam, lParam );
}

int main()
{
    CoInitializeEx( nullptr, COINIT_APARTMENTTHREADED );
    SetProcessDpiAwarenessContext( DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 );
    INITCOMMONCONTROLSEX common{ sizeof( common ), ICC_STANDARD_CLASSES }; InitCommonControlsEx( &common );
    WNDCLASSW type{}; type.lpfnWndProc = WindowProc; type.hInstance = GetModuleHandleW( nullptr );
    type.lpszClassName = L"KiCadCaptionActionFixture"; type.hCursor = LoadCursorW( nullptr, IDC_ARROW );
    if( !RegisterClassW( &type ) ) return 2;
    try
    {
        for( int index = 0; index < 2; ++index )
        {
            auto& frame = frames[index]; frame.index = index;
            auto title = L"KiCad caption fixture " + std::to_wstring( index );
            frame.window = CreateWindowW( type.lpszClassName, title.c_str(), WS_OVERLAPPEDWINDOW,
                    80 + index * 140, 100 + index * 140, 760, 420, nullptr, nullptr, type.hInstance, &frame );
            if( !frame.window ) throw std::runtime_error( "Window creation failed." );
            frame.set = KIPLATFORM::UI::AddWindowsCaptionAction( frame.window, L"Update", [&frame]
            {
                ++frame.clicks;
                auto title = L"KiCad caption fixture " + std::to_wstring( frame.index ) + L" - clicks " + std::to_wstring( frame.clicks );
                SetWindowTextW( frame.window, title.c_str() );
            } );
            ShowWindow( frame.window, SW_SHOW ); UpdateWindow( frame.window );
        }
        // Real window messages also survive the native system-menu modal loop;
        // bare thread messages are not dispatched there.
        HWND control = CreateWindowW( type.lpszClassName, L"", 0, 0, 0, 0, 0, HWND_MESSAGE, nullptr, type.hInstance, nullptr );
        if( !control ) throw std::runtime_error( "Cannot create the test-only command receiver." );
        auto initial = Snapshot();
        std::thread input( [control]
        {
            std::string line;
            while( std::getline( std::cin, line ) )
            {
                auto command = std::make_unique<std::string>( line );
                if( !PostMessageW( control, WM_APP, 0, reinterpret_cast<LPARAM>( command.get() ) ) ) break;
                command.release();
            }
            PostMessageW( control, WM_APP + 1, 0, 0 );
        } );
        std::cout << initial.dump() << '\n' << std::flush;
        MSG message;
        while( GetMessageW( &message, nullptr, 0, 0 ) > 0 )
        { TranslateMessage( &message ); DispatchMessageW( &message ); }
        input.join();
        DestroyWindow( control );
        for( auto& frame : frames ) if( frame.window ) DestroyWindow( frame.window );
        CoUninitialize(); return 0;
    }
    catch( const std::exception& error ) { std::cerr << error.what() << '\n'; return 3; }
}
