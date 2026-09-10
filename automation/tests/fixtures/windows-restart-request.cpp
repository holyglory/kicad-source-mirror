// Execute the production native request builder, keeping this process alive
// while the managed test checks its exact kernel identity and request schema.
#include "automation_update_windows.h"
#include <filesystem>
#include <iostream>

int wmain( int argc, wchar_t** argv )
{
    if( argc != 6 ) return 1;
    try
    {
        auto utf8 = []( const wchar_t* value )
        {
            const auto bytes = std::filesystem::path( value ).u8string();
            return std::string( bytes.begin(), bytes.end() );
        };
        auto request = AUTOMATION_WINDOWS_UPDATE::RestartRequest( utf8( argv[1] ), utf8( argv[2] ),
                std::string( 64, 'a' ), utf8( argv[3] ), utf8( argv[4] ), std::wstring( argv[5] ) == L"true" );
        std::cout << request.dump() << '\n' << std::flush;
        std::string line;
        std::getline( std::cin, line );
        return 0;
    }
    catch( const std::exception& error )
    { std::cerr << error.what() << '\n'; return 2; }
}
