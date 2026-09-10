#include <windows.h>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <string>

int main( int argc, char** argv )
{
    wchar_t executable[32768];
    DWORD size = GetModuleFileNameW( nullptr, executable, 32768 );
    if( size == 0 || size >= 32768 ) return 2;
    std::ifstream policy( std::filesystem::path( executable ).parent_path() / "fixture-policy.txt" );
    std::string mode, commit; std::getline( policy, mode ); std::getline( policy, commit );
    if( mode == "fail" ) { std::cerr << "Explicit staging fixture failure"; return 7; }
    if( mode == "wait" ) { Sleep( 300000 ); return 8; }
    if( mode == "excess-output" ) { std::cout << std::string( 70000, 'x' ); return 0; }
    if( argc == 4 && std::string( argv[1] ) == "version" ) { std::cout << commit; return 0; }
    if( argc == 2 && std::string( argv[1] ) == "--runtime-info" )
    {
        if( GetEnvironmentVariableA( "KICAD_AUTOMATION_NNG_LIBRARY", nullptr, 0 ) ) return 9;
        std::cout << "{\"schemaVersion\":1,\"status\":\"runtime_available\","
            "\"processArchitecture\":\"X64\",\"framework\":\".NET 10.0 synthetic fixture\","
            "\"nngVersion\":\"synthetic fixture, not actual NNG\","
            "\"nativeEditorContacted\":false,\"crossPlatformReady\":false}";
        return 0;
    }
    return 3;
}
