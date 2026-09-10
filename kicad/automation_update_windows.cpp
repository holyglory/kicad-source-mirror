/* Copyright The KiCad Developers, see AUTHORS.txt for contributors.
 * SPDX-License-Identifier: GPL-2.0-or-later
 */
#include "automation_update_windows.h"

#ifdef _WIN32
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <filesystem>
#include <fstream>
#include <set>
#include <stdexcept>

namespace
{
std::string utf8( const std::filesystem::path& path )
{
    const auto value = path.u8string();
    return { value.begin(), value.end() };
}

bool uuid( const std::string& value )
{
    if( value.size() != 36 || value == "00000000-0000-0000-0000-000000000000" ) return false;
    for( size_t i = 0; i < value.size(); ++i )
    {
        if( i == 8 || i == 13 || i == 18 || i == 23 )
        { if( value[i] != '-' ) return false; }
        else if( std::string( "0123456789abcdefABCDEF" ).find( value[i] ) == std::string::npos ) return false;
    }
    return true;
}

std::string currentSelection( const std::filesystem::path& root )
{
    // Read a bounded record through one file handle. An atomic replacement by
    // another editor yields one complete selection, never a mixed snapshot.
    std::ifstream input( root / L"manager" / L"current.json", std::ios::binary );
    if( !input ) throw std::runtime_error( "The current Windows update selection cannot be read." );
    char buffer[4097];
    input.read( buffer, sizeof( buffer ) );
    if( input.bad() || input.gcount() < 1 || input.gcount() > 4096 )
        throw std::runtime_error( "Invalid Windows update selection size." );
    std::set<std::string> keys;
    const auto selected = nlohmann::json::parse( buffer, buffer + input.gcount(),
            [&keys]( int depth, nlohmann::json::parse_event_t event, nlohmann::json& item )
            {
                if( depth > 1 || event == nlohmann::json::parse_event_t::array_start
                    || ( event == nlohmann::json::parse_event_t::key
                         && !keys.insert( item.get<std::string>() ).second ) )
                    throw std::runtime_error( "Invalid Windows update selection structure." );
                return true;
            } );
    if( !selected.is_object() || selected.size() != 3 || selected.value( "schemaVersion", 0 ) != 1
        || !selected.contains( "selectionId" ) || !selected.at( "selectionId" ).is_string()
        || !selected.contains( "versionTarget" ) || !selected.at( "versionTarget" ).is_string() )
        throw std::runtime_error( "Invalid Windows update selection." );
    const auto selection = selected.at( "selectionId" ).get<std::string>();
    const auto target = selected.at( "versionTarget" ).get<std::string>();
    if( !uuid( selection ) || target.size() != 81 || target.substr( 0, 9 ) != "versions/"
        || target.substr( 73 ) != "/payload"
        || target.substr( 9, 64 ).find_first_not_of( "0123456789abcdef" ) != std::string::npos )
        throw std::runtime_error( "Invalid retained Windows update selection." );
    return selection;
}
}

nlohmann::json AUTOMATION_WINDOWS_UPDATE::RestartRequest( const std::string& aInstallationRoot,
        const std::string& aProjectPath, const std::string& aManifestSha256,
        const std::string& aOperationId, const std::string& aInstanceId,
        bool aSoftwareRendering )
{
    const auto root = std::filesystem::u8path( aInstallationRoot );
    // The agreed Windows store and IPC are local, not a network-share bridge.
    if( !root.is_absolute() || root.root_name().native().size() != 2
        || root.root_name().native()[1] != L':' || !uuid( aOperationId ) || !uuid( aInstanceId )
        || aManifestSha256.size() != 64
        || aManifestSha256.find_first_not_of( "0123456789abcdef" ) != std::string::npos
        || ( !aProjectPath.empty() && ( !std::filesystem::u8path( aProjectPath ).is_absolute()
                || !std::filesystem::is_regular_file( std::filesystem::u8path( aProjectPath ) ) ) ) )
        throw std::runtime_error( "Invalid Windows update target." );
    const auto selection = currentSelection( root );
    FILETIME created, exited, kernel, user;
    wchar_t executable[32768];
    DWORD length = 32768;
    if( !GetProcessTimes( GetCurrentProcess(), &created, &exited, &kernel, &user )
        || !QueryFullProcessImageNameW( GetCurrentProcess(), 0, executable, &length ) )
        throw std::runtime_error( "Windows kernel process identity is unavailable." );
    ULARGE_INTEGER creation;
    creation.LowPart = created.dwLowDateTime; creation.HighPart = created.dwHighDateTime;
    const auto processPath = utf8( std::filesystem::path( std::wstring( executable, length ) ) );
    // A short, drive-qualified local NNG pipe name; no file is created here.
    const auto socket = utf8( root.root_path() / ( "kcu-" + aOperationId + ".sock" ) );
    return { { "schemaVersion", 3 }, { "request", {
        { "installationRoot", aInstallationRoot }, { "expectedSelectionId", selection },
        { "manifestSha256", aManifestSha256 }, { "operationId", aOperationId },
        { "oldProcess", { { "processId", GetCurrentProcessId() },
            { "creationFileTime", std::to_string( creation.QuadPart ) }, { "executable", processPath } } },
        { "projectPath", aProjectPath }, { "instanceId", aInstanceId },
        { "socketPath", socket }, { "softwareRendering", aSoftwareRendering } } } };
}
#endif
