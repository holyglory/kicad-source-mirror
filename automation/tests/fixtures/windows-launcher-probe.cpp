// Synthetic launcher protocol probe. Not a KiCad or MCP implementation.
#include <windows.h>
#include <nlohmann/json.hpp>
#include <iostream>
#include <fstream>
#include <string>
#include <vector>

static std::string Utf8( const wchar_t* input )
{
    int size = WideCharToMultiByte( CP_UTF8, WC_ERR_INVALID_CHARS, input, -1, nullptr, 0, nullptr, nullptr );
    if( size <= 0 ) throw std::runtime_error( "Invalid UTF-16 input" );
    std::string result( size, '\0' );
    WideCharToMultiByte( CP_UTF8, WC_ERR_INVALID_CHARS, input, -1, result.data(), size, nullptr, nullptr );
    result.resize( result.size() - 1 ); return result;
}

int wmain( int argc, wchar_t** argv )
{
    std::vector<std::string> arguments;
    for( int index = 1; index < argc; ++index ) arguments.push_back( Utf8( argv[index] ) );
    bool native = argc > 5 && std::wstring( argv[5] ) == L"native";
    std::string input, line;
    if( !native ) while( std::getline( std::cin, line ) ) input += line + "\n";
    nlohmann::json result = { { "synthetic", true }, { "arguments", arguments }, { "input", input } };
    std::ofstream file( "bootstrap-probe.json" ); file << result.dump(); file.close();
    std::cout << result.dump() << '\n';
    return native ? 0 : 17;
}
