// Query-only native control for the managed Windows process identity binding.
#include <windows.h>
#include <nlohmann/json.hpp>
#include <iostream>
#include <string>

int wmain( int argc, wchar_t** argv )
{
    if( argc == 2 && std::wstring( argv[1] ) == L"wait" )
    { std::cout << "ready\n" << std::flush; std::string line; std::getline( std::cin, line ); return 0; }
    if( argc != 2 ) return 1;
    DWORD pid = static_cast<DWORD>( std::stoul( argv[1] ) );
    HANDLE process = OpenProcess( PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, pid );
    if( !process ) return 2;
    FILETIME created, exited, kernel, user; wchar_t path[32768]; DWORD size = 32768;
    if( WaitForSingleObject( process, 0 ) != WAIT_TIMEOUT || !GetProcessTimes( process, &created, &exited, &kernel, &user )
        || !QueryFullProcessImageNameW( process, 0, path, &size ) ) { CloseHandle( process ); return 3; }
    ULARGE_INTEGER time; time.LowPart = created.dwLowDateTime; time.HighPart = created.dwHighDateTime;
    int count = WideCharToMultiByte( CP_UTF8, WC_ERR_INVALID_CHARS, path, size, nullptr, 0, nullptr, nullptr );
    if( count <= 0 ) { CloseHandle( process ); return 4; }
    std::string utf8( count, '\0' ); WideCharToMultiByte( CP_UTF8, WC_ERR_INVALID_CHARS, path, size, utf8.data(), count, nullptr, nullptr );
    nlohmann::json result = { { "processId", pid }, { "creationFileTime", std::to_string( time.QuadPart ) }, { "executable", utf8 } };
    CloseHandle( process ); std::cout << result.dump() << '\n'; return 0;
}
