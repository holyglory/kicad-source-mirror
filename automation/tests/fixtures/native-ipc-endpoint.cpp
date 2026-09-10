#include <api/api_socket_url.h>
#include <iostream>

#ifdef _WIN32
#include <windows.h>
#endif

int main( int argc, char** argv )
{
    if( argc != 3 ) return 1;
    const std::string endpoint = KiApiSocketUrl( argv[1] );
    if( endpoint != argv[2] ) return 2;

#ifdef _WIN32
    // This is a native local-pipe naming test, not a simulated KiCad session.
    const std::string pipeName = "\\\\.\\pipe\\" + endpoint.substr( 6 );
    HANDLE server = CreateNamedPipeA( pipeName.c_str(), PIPE_ACCESS_DUPLEX,
            PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
            1, 4096, 4096, 0, nullptr );
    if( server == INVALID_HANDLE_VALUE ) return 3;
    HANDLE client = CreateFileA( pipeName.c_str(), GENERIC_READ | GENERIC_WRITE,
            0, nullptr, OPEN_EXISTING, 0, nullptr );
    if( client == INVALID_HANDLE_VALUE ) { CloseHandle( server ); return 4; }
    const char expected[] = { 0, 42, 127 };
    char received[3] = {};
    DWORD written = 0, read = 0;
    const bool exchanged = WriteFile( client, expected, 3, &written, nullptr ) && written == 3
            && ReadFile( server, received, 3, &read, nullptr ) && read == 3
            && std::equal( std::begin( expected ), std::end( expected ), std::begin( received ) );
    CloseHandle( client );
    CloseHandle( server );
    if( !exchanged ) return 5;

    // The previous path spelling must fail for its actual backslash naming
    // problem; unrelated failures are not regression evidence.
    const std::string legacyName = "\\\\.\\pipe\\" + std::string( argv[1] );
    HANDLE legacy = CreateNamedPipeA( legacyName.c_str(), PIPE_ACCESS_DUPLEX,
            PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT, 1, 4096, 4096, 0, nullptr );
    if( legacy != INVALID_HANDLE_VALUE ) { CloseHandle( legacy ); return 6; }
    if( GetLastError() != ERROR_INVALID_NAME ) return 7;
    std::cout << "native-pipe-exchange-and-legacy-rejection\n";
#else
    std::cout << "native-posix-endpoint-unchanged\n";
#endif
}
