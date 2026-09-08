/* Copyright The KiCad Developers, see AUTHORS.txt for contributors.
 * SPDX-License-Identifier: GPL-2.0-or-later
 */
#include "automation_update_client.h"
#include <wx/filename.h>
#include <wx/stream.h>
#include <wx/utils.h>
#include <wx/ffile.h>
#include <wx/stdpaths.h>
#include <boost/uuid/random_generator.hpp>
#include <boost/uuid/uuid_io.hpp>
#include <fstream>
#include <sstream>
#include <filesystem>
#include <utility>

namespace
{
class UPDATE_PROCESS : public wxProcess
{
public:
    explicit UPDATE_PROCESS( wxEvtHandler* owner ) : wxProcess( owner ), m_reaper( this )
    {
        Bind( wxEVT_TIMER, [this]( wxTimerEvent& )
              { wxProcess::Kill( GetPid(), wxSIGKILL, wxKILL_CHILDREN ); }, m_reaper.GetId() );
    }

    void ReleaseOwner()
    {
        wxProcess::Kill( GetPid(), wxSIGINT, wxKILL_CHILDREN );
        Detach();
        m_reaper.StartOnce( 2000 );
    }

private:
    wxTimer m_reaper;
};

bool digest( const nlohmann::json& message )
{
    if( !message.contains( "manifestSha256" ) || !message.at( "manifestSha256" ).is_string() )
        return false;
    const auto value = message.at( "manifestSha256" ).get<std::string>();
    return value.size() == 64 && value.find_first_not_of( "0123456789abcdef" ) == std::string::npos;
}
}

AUTOMATION_UPDATE_CLIENT::AUTOMATION_UPDATE_CLIENT( wxString aHelper, wxString aConfiguration,
                                                   OBSERVER aObserver ) :
        m_helper( std::move( aHelper ) ), m_configuration( std::move( aConfiguration ) ),
        m_observer( std::move( aObserver ) ), m_ioTimer( this ), m_checkTimer( this )
{
    Bind( wxEVT_TIMER, &AUTOMATION_UPDATE_CLIENT::poll, this, m_ioTimer.GetId() );
    Bind( wxEVT_TIMER, &AUTOMATION_UPDATE_CLIENT::scheduledCheck, this, m_checkTimer.GetId() );
    Bind( wxEVT_END_PROCESS, &AUTOMATION_UPDATE_CLIENT::finished, this );
}

AUTOMATION_UPDATE_CLIENT::~AUTOMATION_UPDATE_CLIENT()
{
    m_ioTimer.Stop();
    m_checkTimer.Stop();
    if( m_process )
    {
        // This child has its own group and cannot contain editor processes.
        static_cast<UPDATE_PROCESS*>( m_process.get() )->ReleaseOwner();
        m_process.release(); // wxProcess owns deletion after detached termination
    }
}

void AUTOMATION_UPDATE_CLIENT::Start( int aCheckIntervalMs )
{
    if( aCheckIntervalMs < 1 )
    {
        fail( "The update check interval must be positive." );
        return;
    }
    m_checkTimer.Start( aCheckIntervalMs );
    Check();
}

void AUTOMATION_UPDATE_CLIENT::Check()
{
    if( !m_process )
    {
        m_restart = false;
        launch( false );
    }
}

bool AUTOMATION_UPDATE_CLIENT::Restart( const wxString& aProjectPath, const std::string& aInstanceId,
                                       bool aSoftwareRendering )
{
#ifdef __linux__
    if( m_process || !m_candidate.is_object() )
        return false;
    try
    {
        m_invalid = false;
        std::ifstream file( m_configuration.ToStdString() );
        nlohmann::json configuration;
        file >> configuration;
        std::string root = configuration.at( "installationRoot" ).get<std::string>();
        if( !wxFileName( wxString::FromUTF8( root ) ).IsAbsolute() || !wxFileName( aProjectPath ).IsAbsolute()
                || !wxFileName::FileExists( aProjectPath ) )
            throw std::runtime_error( "Invalid installation or project path." );
        const auto operation = boost::uuids::to_string( boost::uuids::random_generator()() );
        const auto instance = aInstanceId.empty() ? boost::uuids::to_string( boost::uuids::random_generator()() ) : aInstanceId;
        // Another live project may have selected this same candidate already.
        // Snapshot the current selection for this click, not for the earlier
        // background download. The helper still rejects changes after this point.
        const auto selection = std::filesystem::read_symlink( root + "/manager/current" ).generic_string();
        std::ifstream bootFile( "/proc/sys/kernel/random/boot_id" );
        std::string boot;
        bootFile >> boot;
        std::ifstream statFile( "/proc/self/stat" );
        std::string stat;
        std::getline( statFile, stat );
        const auto end = stat.rfind( ')' );
        if( boot.empty() || end == std::string::npos ) throw std::runtime_error( "Process identity is unavailable." );
        std::istringstream fields( stat.substr( end + 1 ) );
        std::string field;
        for( int index = 0; index < 19; ++index ) fields >> field;
        uint64_t ticks = 0;
        if( !( fields >> ticks ) ) throw std::runtime_error( "Process start counter is unavailable." );
        wxString socket = wxStandardPaths::Get().GetTempDir() + wxFILE_SEP_PATH
                          + wxString::FromUTF8( "kcu-" + operation.substr( 0, 12 ) + ".sock" );
        m_restartConfiguration = wxString::FromUTF8( root ) + wxFILE_SEP_PATH + "state"
                                 + wxFILE_SEP_PATH + wxString::FromUTF8( "restart-" + operation + ".json" );
        nlohmann::json request = { { "schemaVersion", 1 }, { "request", {
            { "installationRoot", root }, { "expectedTarget", selection },
            { "manifestSha256", m_candidate.at( "manifestSha256" ) }, { "operationId", operation },
            { "oldProcess", { { "processId", wxGetProcessId() }, { "bootId", boot }, { "startTicks", ticks } } },
            { "projectPath", aProjectPath.ToStdString() }, { "instanceId", instance },
            { "socketPath", socket.ToStdString() }, { "softwareRendering", aSoftwareRendering } } } };
        wxFFile output( m_restartConfiguration, "wx" );
        if( !output.IsOpened() || !output.Write( wxString::FromUTF8( request.dump() ) ) || !output.Flush() )
            throw std::runtime_error( "Cannot write the update restart request." );
        output.Close();
        m_restart = true;
        launch( false );
        return m_process != nullptr;
    }
    catch( const std::exception& error )
    {
        fail( error.what() );
        return false;
    }
#else
    return false;
#endif
}

void AUTOMATION_UPDATE_CLIENT::DetachForRestart()
{
    if( !m_restart || !m_process ) return;
    m_ioTimer.Stop();
    m_checkTimer.Stop();
    m_process->Detach();
    m_process.release();
    m_pid = 0;
}

void AUTOMATION_UPDATE_CLIENT::scheduledCheck( wxTimerEvent& )
{
    Check();
}

void AUTOMATION_UPDATE_CLIENT::launch( bool aPrepare )
{
    if( m_process )
        return;
    m_invalid = false;
    if( !wxFileName( m_helper ).IsAbsolute() || !wxFileName::FileExists( m_helper )
            || !wxFileName( m_configuration ).IsAbsolute() || !wxFileName::FileExists( m_configuration ) )
    {
        fail( "Use an existing absolute updater helper and installed configuration." );
        return;
    }
    m_prepare = aPrepare;
    m_cancelAt = -1;
    m_invalid = false;
    m_stdout.clear();
    m_stderr.clear();
    m_terminal = nullptr;
    m_process = std::make_unique<UPDATE_PROCESS>( this );
    m_process->Redirect();
    wxString operation = m_restart ? wxS( "--restart-update" ) : aPrepare ? wxS( "--prepare-update" ) : wxS( "--check-update" );
    wxString configuration = m_restart ? m_restartConfiguration : m_configuration;
    const wxChar* arguments[] = { m_helper.c_str(), operation.c_str(), wxS( "--configuration" ),
                                  configuration.c_str(), nullptr };
    m_pid = wxExecute( arguments, wxEXEC_ASYNC | wxEXEC_MAKE_GROUP_LEADER, m_process.get() );
    if( m_pid <= 0 )
    {
        m_process.reset();
        fail( "The updater helper could not be started." );
        return;
    }
    m_elapsed.Start();
    m_ioTimer.Start( 100 );
    notify( { { "schemaVersion", 1 }, { "status", "helper_started" },
              { "operation", m_restart ? "restart" : aPrepare ? "prepare" : "check" }, { "installationReady", false },
              { "helperPid", m_pid } } );
}

void AUTOMATION_UPDATE_CLIENT::Cancel()
{
    if( m_process && m_cancelAt < 0 )
    {
        m_cancelAt = m_elapsed.Time();
        wxProcess::Kill( m_pid, wxSIGINT, wxKILL_CHILDREN );
    }
}

void AUTOMATION_UPDATE_CLIENT::poll( wxTimerEvent& )
{
    drain();
    if( !m_process )
        return;
    if( m_elapsed.Time() >= 15 * 60 * 1000 && m_cancelAt < 0 )
    {
        fail( "The updater helper exceeded its deadline." );
        Cancel();
    }
    if( m_cancelAt >= 0 && m_elapsed.Time() - m_cancelAt >= 2000 )
        wxProcess::Kill( m_pid, wxSIGKILL, wxKILL_CHILDREN );
}

void AUTOMATION_UPDATE_CLIENT::readStream( wxInputStream* aStream, std::string& aBuffer )
{
    if( !aStream )
        return;
    char bytes[4096];
    while( aStream->CanRead() )
    {
        aStream->Read( bytes, sizeof( bytes ) );
        size_t count = aStream->LastRead();
        if( !count )
            break;
        if( aBuffer.size() + count > 128 * 1024 )
        {
            fail( "The updater helper exceeded its output limit." );
            Cancel();
            return;
        }
        aBuffer.append( bytes, count );
    }
}

void AUTOMATION_UPDATE_CLIENT::drain()
{
    if( !m_process )
        return;
    readStream( m_process->GetInputStream(), m_stdout );
    readStream( m_process->GetErrorStream(), m_stderr );
    size_t newline;
    while( ( newline = m_stdout.find( '\n' ) ) != std::string::npos )
    {
        std::string line = m_stdout.substr( 0, newline );
        m_stdout.erase( 0, newline + 1 );
        if( m_invalid )
            continue;
        try
        {
            auto message = nlohmann::json::parse( line );
            if( m_restart )
            {
                if( !message.is_object() || message.value( "schemaVersion", 0 ) != 1 )
                    throw std::runtime_error( "Unexpected restart response." );
                if( message.contains( "state" ) && message.at( "state" ).value( "status", "" ) == "waiting_for_exit" )
                {
                    m_terminal = message.at( "state" );
                    // The close prompt may run another event loop; invoke it
                    // after draining, with normal native ownership still intact.
                    CallAfter( [this]
                    {
                        if( m_process && m_restart && m_cancelAt < 0 )
                            notify( { { "schemaVersion", 1 }, { "status", "restart_waiting" }, { "journal", m_terminal } } );
                    } );
                }
                else if( message.value( "status", "" ) == "failed" )
                {
                    m_terminal = message;
                    notify( message );
                }
                else throw std::runtime_error( "Restart did not acknowledge a waiting state." );
                continue;
            }
            if( !message.is_object() || message.value( "schemaVersion", 0 ) != 1
                    || !message.contains( "installationReady" )
                    || message.at( "installationReady" ).get<bool>()
                    || !message.contains( "status" ) || !message.at( "status" ).is_string() )
                throw std::runtime_error( "Unexpected helper response." );
            if( !m_terminal.is_null() )
                throw std::runtime_error( "Response after terminal result." );
            std::string status = message.at( "status" ).get<std::string>();
            if( status == "available" || status == "candidate_registered" )
            {
                if( !digest( message ) )
                    throw std::runtime_error( "Missing candidate identity." );
                if( status == "candidate_registered"
                    && ( !message.contains( "directory" ) || !message.at( "directory" ).is_string()
                         || !wxFileName( wxString::FromUTF8( message.at( "directory" ).get<std::string>() ) ).IsAbsolute()
                         || !message.contains( "expectedTarget" ) || !message.at( "expectedTarget" ).is_string() ) )
                    throw std::runtime_error( "Incomplete registered candidate." );
            }
            if( status == "available" || status == "up_to_date" || status == "target_unavailable"
                || status == "preparation_unavailable" || status == "archive_staged"
                || status == "candidate_registered" || status == "failed" || status == "cancelled" )
                m_terminal = message;
            else if( status != "checking" && status != "downloading" && status != "staging"
                     && status != "checking_native_identity" && status != "registering_candidate" )
                throw std::runtime_error( "Unknown progress state." );
            notify( message );
        }
        catch( const std::exception& )
        {
            fail( "The updater helper returned an invalid response." );
            Cancel();
        }
    }
}

void AUTOMATION_UPDATE_CLIENT::finished( wxProcessEvent& aEvent )
{
    if( !m_process || aEvent.GetPid() != m_pid )
        return;
    drain();
    m_ioTimer.Stop();
    m_process.reset();
    m_pid = 0;
    if( m_invalid )
    {
        notify( { { "schemaVersion", 1 }, { "status", "helper_finished" }, { "installationReady", false } } );
        return;
    }
    if( m_cancelAt >= 0 || aEvent.GetExitCode() == 2 )
    {
        notify( { { "schemaVersion", 1 }, { "status", "cancelled" }, { "installationReady", false } } );
        return;
    }
    if( !m_stdout.empty() || !m_terminal.is_object() )
    {
        fail( "The updater helper exited without a complete result." );
        return;
    }
    std::string status = m_terminal.value( "status", "" );
    if( aEvent.GetExitCode() != 0 )
    {
        if( status != "failed" )
            fail( "The updater helper exited unsuccessfully." );
        else
            notify( m_terminal ); // release the native action's busy state
        return;
    }
    if( status == "failed" || status == "cancelled" )
    {
        fail( "The updater helper exit code contradicts its result." );
        return;
    }
    if( !m_prepare && status == "available" )
    {
        if( !m_candidate.is_object() || m_candidate.value( "manifestSha256", "" )
                                      != m_terminal.value( "manifestSha256", "" ) )
            CallAfter( [this] { launch( true ); } );
        else
            notify( { { "schemaVersion", 1 }, { "status", "candidate_available" } } );
    }
    else if( m_prepare && status == "candidate_registered" )
    {
        m_candidate = m_terminal;
        notify( { { "schemaVersion", 1 }, { "status", "candidate_available" } } );
    }
    else if( status == "up_to_date" )
    {
        m_candidate = nullptr;
        notify( { { "schemaVersion", 1 }, { "status", "candidate_cleared" } } );
    }
    else if( status != "up_to_date" && status != "target_unavailable" && status != "preparation_unavailable"
             && status != "archive_staged" )
        fail( "The updater helper returned an unexpected terminal state." );
}

void AUTOMATION_UPDATE_CLIENT::notify( const nlohmann::json& aMessage )
{
    if( m_observer )
        m_observer( aMessage );
}

void AUTOMATION_UPDATE_CLIENT::fail( const std::string& aReason )
{
    if( m_invalid )
        return;
    m_invalid = true;
    notify( { { "schemaVersion", 1 }, { "status", "failed" }, { "installationReady", false },
              { "error", { { "kind", "native_helper_failure" }, { "message", aReason } } } } );
}
