// Query-only external native UI evidence, bound to the exact kernel process.
// The parent harness owns a bounded lifetime for this observer, not the editor.
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <oleacc.h>
#include <nlohmann/json.hpp>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <string>

using json = nlohmann::json;
std::string Utf8( const std::wstring& value )
{
    auto bytes = std::filesystem::path( value ).u8string(); return { bytes.begin(), bytes.end() };
}
std::string Name( HWND window )
{
    wchar_t text[1024]; int count = GetWindowTextW( window, text, 1024 ); return Utf8( std::wstring( text, count ) );
}
struct SCOPE { DWORD pid; json windows = json::array(), buttons = json::array(); };
struct CHILD_SCOPE { SCOPE* scope; HWND root; };

void Button( SCOPE& scope, HWND root, HWND control, std::string name, bool enabled, bool visible, RECT rect )
{
    std::string label;
    for( size_t i = 0; i < name.size(); ++i )
        if( name[i] != '&' || ( i + 1 < name.size() && name[i + 1] == '&' && ++i ) ) label += name[i];
    scope.buttons.push_back( { { "window", std::to_string( reinterpret_cast<uintptr_t>( root ) ) },
        { "control", std::to_string( reinterpret_cast<uintptr_t>( control ) ) }, { "title", label },
        { "enabled", enabled && IsWindowEnabled( root ) }, { "visible", visible && IsWindowVisible( root ) && !IsIconic( root ) && !IsRectEmpty( &rect ) },
        { "rectangle", { { "x", rect.left }, { "y", rect.top }, { "width", rect.right - rect.left }, { "height", rect.bottom - rect.top } } } } );
}

BOOL CALLBACK Child( HWND child, LPARAM data )
{
    auto& context = *reinterpret_cast<CHILD_SCOPE*>( data );
    DWORD pid = 0; GetWindowThreadProcessId( child, &pid );
    wchar_t type[128]; GetClassNameW( child, type, 128 );
    if( pid == context.scope->pid && std::wstring( type ) == L"Button" )
    {
        RECT rect{}; GetWindowRect( child, &rect );
        Button( *context.scope, context.root, child, Name( child ), IsWindowEnabled( child ), IsWindowVisible( child ), rect );
    }
    return TRUE;
}

BOOL CALLBACK Window( HWND window, LPARAM data )
{
    auto& scope = *reinterpret_cast<SCOPE*>( data );
    DWORD pid = 0; GetWindowThreadProcessId( window, &pid );
    if( pid != scope.pid || !IsWindowVisible( window ) || IsIconic( window ) ) return TRUE;
    scope.windows.push_back( { { "handle", std::to_string( reinterpret_cast<uintptr_t>( window ) ) }, { "title", Name( window ) } } );
    CHILD_SCOPE children{ &scope, window }; EnumChildWindows( window, Child, reinterpret_cast<LPARAM>( &children ) );
    IAccessible* object = nullptr;
    if( SUCCEEDED( AccessibleObjectFromWindow( window, OBJID_TITLEBAR, IID_IAccessible, reinterpret_cast<void**>( &object ) ) ) && object )
    {
        long count = 0;
        if( SUCCEEDED( object->get_accChildCount( &count ) ) && count >= 0 && count <= 64 )
            for( long index = 1; index <= count; ++index )
            {
                VARIANT child{}, role{}, state{}; child.vt = VT_I4; child.lVal = index;
                BSTR name = nullptr;
                if( FAILED( object->get_accRole( child, &role ) ) || role.vt != VT_I4 || role.lVal != ROLE_SYSTEM_PUSHBUTTON ) continue;
                if( FAILED( object->get_accName( child, &name ) ) || !name ) continue;
                auto label = Utf8( name ); SysFreeString( name );
                if( FAILED( object->get_accState( child, &state ) ) || state.vt != VT_I4 ) continue;
                long x = 0, y = 0, width = 0, height = 0;
                if( FAILED( object->accLocation( &x, &y, &width, &height, child ) ) ) continue;
                Button( scope, window, nullptr, label, !( state.lVal & STATE_SYSTEM_UNAVAILABLE ),
                    !( state.lVal & ( STATE_SYSTEM_INVISIBLE | STATE_SYSTEM_OFFSCREEN ) ), { x, y, x + width, y + height } );
            }
        object->Release();
    }
    return TRUE;
}

int wmain( int argc, wchar_t** argv )
{
    if( argc != 2 ) return 1;
    HANDLE process = nullptr;
    try
    {
        std::ifstream input( std::filesystem::path( argv[1] ), std::ios::binary );
        char buffer[16385]; input.read( buffer, sizeof( buffer ) );
        if( input.bad() || input.gcount() < 1 || input.gcount() > 16384 ) throw std::runtime_error( "Invalid observer request size." );
        auto request = json::parse( buffer, buffer + input.gcount() );
        if( request.at( "schemaVersion" ) != 1 || !request.at( "creationFileTime" ).is_string() ) throw std::runtime_error( "Invalid observer request." );
        DWORD pid = request.at( "processId" ).get<DWORD>();
        process = OpenProcess( PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, pid );
        FILETIME creation, exit, kernel, user; wchar_t executable[32768]; DWORD length = 32768;
        if( !process || WaitForSingleObject( process, 0 ) != WAIT_TIMEOUT
            || !GetProcessTimes( process, &creation, &exit, &kernel, &user )
            || !QueryFullProcessImageNameW( process, 0, executable, &length ) ) throw std::runtime_error( "The exact live process is unavailable." );
        ULARGE_INTEGER stamp; stamp.LowPart = creation.dwLowDateTime; stamp.HighPart = creation.dwHighDateTime;
        if( request.at( "creationFileTime" ) != std::to_string( stamp.QuadPart )
            || request.at( "executable" ) != Utf8( std::wstring( executable, length ) ) ) throw std::runtime_error( "The native UI target identity changed." );
        CoInitializeEx( nullptr, COINIT_APARTMENTTHREADED );
        SetThreadDpiAwarenessContext( DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 );
        SCOPE scope{ pid }; EnumWindows( Window, reinterpret_cast<LPARAM>( &scope ) );
        if( WaitForSingleObject( process, 0 ) != WAIT_TIMEOUT ) throw std::runtime_error( "The editor exited during UI observation." );
        std::cout << json( { { "schemaVersion", 1 }, { "status", "observed" }, { "windows", scope.windows },
            { "buttons", scope.buttons }, { "nativeMutation", false } } ).dump() << '\n';
        CloseHandle( process ); CoUninitialize(); return 0;
    }
    catch( const std::exception& error )
    {
        if( process ) CloseHandle( process );
        std::cerr << error.what() << '\n'; return 2;
    }
}
