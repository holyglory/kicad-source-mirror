/* KiCad automation launcher, GPL-3.0-or-later. Copyright The KiCad Developers. */
#include <windows.h>
#include <bcrypt.h>
#include <shellapi.h>
#include <nlohmann/json.hpp>
#include <array>
#include <filesystem>
#include <fstream>
#include <set>
#include <string>
#include <vector>

namespace fs = std::filesystem;

static bool Digest( const std::string& value )
{
    return value.size() == 64 && value.find_first_not_of( "0123456789abcdef" ) == std::string::npos;
}

static std::wstring Quote( const std::wstring& value )
{
    std::wstring result = L"\"";
    size_t slashes = 0;
    for( wchar_t character : value )
    {
        if( character == L'\\' ) { ++slashes; continue; }
        result.append( character == L'\"' ? slashes * 2 + 1 : slashes, L'\\' );
        result += character; slashes = 0;
    }
    result.append( slashes * 2, L'\\' ); result += L'\"'; return result;
}

static void Ordinary( const fs::path& path, bool directory )
{
    DWORD attributes = GetFileAttributesW( path.c_str() );
    if( attributes == INVALID_FILE_ATTRIBUTES || ( attributes & FILE_ATTRIBUTE_REPARSE_POINT )
        || bool( attributes & FILE_ATTRIBUTE_DIRECTORY ) != directory )
        throw std::runtime_error( "Missing or redirected launcher data" );
}

static std::string Hash( const fs::path& path )
{
    BCRYPT_ALG_HANDLE algorithm = nullptr;
    BCRYPT_HASH_HANDLE hash = nullptr;
    if( BCryptOpenAlgorithmProvider( &algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0 ) < 0 )
        throw std::runtime_error( "Cannot initialize SHA-256" );
    std::array<unsigned char, 32> digest;
    try
    {
        if( BCryptCreateHash( algorithm, &hash, nullptr, 0, nullptr, 0, 0 ) < 0 )
            throw std::runtime_error( "Cannot initialize the bootstrap hash" );
        std::ifstream file( path, std::ios::binary );
        if( !file ) throw std::runtime_error( "Cannot open the bootstrap helper" );
        std::array<char, 65536> buffer;
        while( file )
        {
            file.read( buffer.data(), buffer.size() );
            if( file.gcount() && BCryptHashData( hash, reinterpret_cast<PUCHAR>( buffer.data() ),
                                               static_cast<ULONG>( file.gcount() ), 0 ) < 0 )
                throw std::runtime_error( "Cannot hash the bootstrap helper" );
        }
        if( !file.eof() || BCryptFinishHash( hash, digest.data(), digest.size(), 0 ) < 0 )
            throw std::runtime_error( "Bootstrap helper hash failed" );
    }
    catch( ... )
    {
        if( hash ) BCryptDestroyHash( hash );
        BCryptCloseAlgorithmProvider( algorithm, 0 ); throw;
    }
    BCryptDestroyHash( hash ); BCryptCloseAlgorithmProvider( algorithm, 0 );
    const char* digits = "0123456789abcdef"; std::string result;
    for( unsigned char value : digest ) { result += digits[value >> 4]; result += digits[value & 15]; }
    return result;
}

static int Launch()
{
    std::vector<wchar_t> module( 32768 );
    DWORD size = GetModuleFileNameW( nullptr, module.data(), static_cast<DWORD>( module.size() ) );
    if( size == 0 || size == module.size() ) throw std::runtime_error( "Cannot identify the installed launcher" );
    fs::path root = fs::path( std::wstring( module.data(), size ) ).parent_path();
    Ordinary( root, true );
    fs::path metadata = root / L"launcher-bootstrap.json"; Ordinary( metadata, false );
    if( fs::file_size( metadata ) > 4096 ) throw std::runtime_error( "Invalid launcher metadata size" );
    std::ifstream file( metadata, std::ios::binary );
    std::set<std::string> keys;
    auto record = nlohmann::json::parse( file, [&]( int, nlohmann::json::parse_event_t event, nlohmann::json& value )
    {
        if( event == nlohmann::json::parse_event_t::key && !keys.insert( value.get<std::string>() ).second )
            throw std::runtime_error( "Repeated bootstrap metadata key" );
        return true;
    } );
    if( !record.is_object() || record.size() != 3 || record.at( "schemaVersion" ) != 1 )
        throw std::runtime_error( "Invalid launcher bootstrap metadata" );
    std::string version = record.at( "versionDigest" ).get<std::string>();
    std::string expected = record.at( "helperSha256" ).get<std::string>();
    if( !Digest( version ) || !Digest( expected ) ) throw std::runtime_error( "Invalid retained bootstrap identity" );
    // This is the initially installed helper, not an unchecked current pointer.
    // The compiled .NET helper authenticates the selected full payload before use.
    fs::path bin = root / L"versions"; Ordinary( bin, true );
    bin /= fs::path( version ); Ordinary( bin, true );
    bin /= L"payload"; Ordinary( bin, true ); bin /= L"bin"; Ordinary( bin, true );
    fs::path helper = bin / L"kicad-mcp.exe"; Ordinary( helper, false );
    if( Hash( helper ) != expected ) throw std::runtime_error( "The installed bootstrap helper changed" );
    int count = 0;
    wchar_t** arguments = CommandLineToArgvW( GetCommandLineW(), &count );
    if( !arguments ) throw std::runtime_error( "Cannot read launcher arguments" );
    std::wstring command = Quote( helper.wstring() ) + L" --launch-installed --installation " + Quote( root.wstring() );
#ifdef KICAD_LAUNCH_GUI
    command += L" --target native --";
#else
    command += L" --target mcp --";
#endif
    for( int index = 1; index < count; ++index ) command += L" " + Quote( arguments[index] );
    LocalFree( arguments );
    if( command.size() >= 32767 ) throw std::runtime_error( "Launcher argument list is too long" );
    STARTUPINFOW startup = {}; startup.cb = sizeof( startup );
    PROCESS_INFORMATION child = {};
    DWORD flags = 0;
#ifdef KICAD_LAUNCH_GUI
    flags = CREATE_NO_WINDOW;
#else
    startup.dwFlags = STARTF_USESTDHANDLES;
    startup.hStdInput = GetStdHandle( STD_INPUT_HANDLE );
    startup.hStdOutput = GetStdHandle( STD_OUTPUT_HANDLE );
    startup.hStdError = GetStdHandle( STD_ERROR_HANDLE );
#endif
    if( !CreateProcessW( helper.c_str(), command.data(), nullptr, nullptr, TRUE, flags,
                        nullptr, nullptr, &startup, &child ) )
        throw std::runtime_error( "Cannot start the verified installation helper" );
    CloseHandle( child.hThread );
    DWORD wait = WaitForSingleObject( child.hProcess, INFINITE ), result = 1;
    if( wait != WAIT_OBJECT_0 || !GetExitCodeProcess( child.hProcess, &result ) ) result = 1;
    CloseHandle( child.hProcess );
#ifdef KICAD_LAUNCH_GUI
    if( result != 0 ) throw std::runtime_error( "The verified KiCad installation could not start. See launch-errors in the installation directory or run kicad-mcp.exe --runtime-info for details." );
#endif
    return static_cast<int>( result );
}

static int Entry()
{
    try { return Launch(); }
    catch( const std::exception& error )
    {
#ifdef KICAD_LAUNCH_GUI
        MessageBoxA( nullptr, error.what(), "KiCad launch failed", MB_OK | MB_ICONERROR );
#else
        std::string message = std::string( "KiCad launch failed: " ) + error.what() + "\n";
        DWORD written; WriteFile( GetStdHandle( STD_ERROR_HANDLE ), message.data(),
                                  static_cast<DWORD>( message.size() ), &written, nullptr );
#endif
        return 1;
    }
}
#ifdef KICAD_LAUNCH_GUI
int WINAPI wWinMain( HINSTANCE, HINSTANCE, PWSTR, int ) { return Entry(); }
#else
int wmain() { return Entry(); }
#endif
