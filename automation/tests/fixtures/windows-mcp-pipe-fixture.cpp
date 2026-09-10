// Synthetic protocol process whose child deliberately holds inherited stderr.
// The native test job owns both processes; this is not a KiCad/MCP server proof.
#include <windows.h>
#include <nlohmann/json.hpp>
#include <fstream>
#include <iostream>
#include <string>

int wmain( int argc, wchar_t** argv )
{
    if( argc == 2 && std::wstring( argv[1] ) == L"--child" )
    {
        std::cerr << "Synthetic child retains stderr until its test job closes.\n" << std::flush;
        HANDLE stop = CreateEventW( nullptr, TRUE, FALSE, nullptr );
        if( !stop ) return 2;
        WaitForSingleObject( stop, INFINITE ); CloseHandle( stop ); return 0;
    }
    wchar_t file[32768]; DWORD length = GetModuleFileNameW( nullptr, file, 32768 );
    if( !length || length == 32768 ) return 3;
    std::wstring command = L"\"" + std::wstring( file, length ) + L"\" --child";
    STARTUPINFOW startup{}; startup.cb = sizeof( startup ); startup.dwFlags = STARTF_USESTDHANDLES;
    startup.hStdInput = GetStdHandle( STD_INPUT_HANDLE ); startup.hStdOutput = GetStdHandle( STD_OUTPUT_HANDLE ); startup.hStdError = GetStdHandle( STD_ERROR_HANDLE );
    PROCESS_INFORMATION child{};
    if( !CreateProcessW( file, command.data(), nullptr, nullptr, TRUE, CREATE_NO_WINDOW, nullptr, nullptr, &startup, &child ) ) return 4;
    { std::ofstream identity( "pipe-child.pid" ); identity << child.dwProcessId; }
    CloseHandle( child.hThread ); CloseHandle( child.hProcess );
    std::string line;
    while( std::getline( std::cin, line ) )
    {
        auto request = nlohmann::json::parse( line );
        if( !request.contains( "id" ) ) continue;
        std::cout << nlohmann::json( { { "jsonrpc", "2.0" }, { "id", request.at( "id" ) },
            { "result", { { "protocolVersion", "2025-06-18" }, { "capabilities", nlohmann::json::object() },
                { "serverInfo", { { "name", "synthetic pipe-lifetime fixture" }, { "version", "1" } } } } } } ).dump() << '\n' << std::flush;
    }
    return 0;
}
