/* Copyright The KiCad Developers, see AUTHORS.txt for contributors.
 * SPDX-License-Identifier: GPL-2.0-or-later
 */
#include <automation_update_client.h>
#include <wx/app.h>
#include <wx/evtloop.h>
#include <wx/filename.h>
#include <wx/init.h>
#include <wx/log.h>
#include <wx/timer.h>
#include <boost/test/unit_test.hpp>
#include <fstream>
#include <iostream>
#include <csignal>
#include <thread>
#include <chrono>
#include <filesystem>

namespace
{
std::string executable;

int fixture( int argc, char** argv )
{
    if( argc != 4 || std::string( argv[2] ) != "--configuration" )
        return 1;
    std::ifstream input( argv[3] );
    std::string mode;
    input >> mode;
    if( mode.starts_with( "{" ) )
    {
        input.clear(); input.seekg( 0 );
        nlohmann::json configuration;
        input >> configuration;
        if( std::string( argv[1] ) == "--restart-update" )
        {
            const auto root = configuration.at( "request" ).at( "installationRoot" ).get<std::string>();
            std::ifstream restartMode( root + "/restart-mode" );
            restartMode >> mode;
            if( mode == "reject" )
            {
                std::cout << R"({"schemaVersion":1,"status":"failed","error":{"message":"Synthetic restart rejection."}})" << std::endl;
                return 1;
            }
            std::cout << R"({"schemaVersion":1,"state":{"status":"waiting_for_exit"}})" << std::endl;
            for( ;; ) std::this_thread::sleep_for( std::chrono::milliseconds( 50 ) );
        }
        mode = configuration.value( "fixtureMode", "success" );
    }
    nlohmann::json message = { { "schemaVersion", 1 }, { "installationReady", false },
                              { "manifestSha256", std::string( 64, '1' ) } };
    if( mode == "hang" || mode == "ignore-interrupt" )
    {
        if( mode == "ignore-interrupt" ) std::signal( SIGINT, SIG_IGN );
        message["status"] = "checking";
        message["fixturePid"] = wxGetProcessId();
        std::cout << message.dump() << std::endl;
        for( ;; ) std::this_thread::sleep_for( std::chrono::milliseconds( 50 ) );
    }
    if( mode == "malformed" ) { std::cout << "bad json\n"; return 0; }
    if( mode == "no-terminal" ) return 0;
    if( mode == "oversize" ) { std::cout << std::string( 140000, 'x' ); return 0; }
    bool prepare = std::string( argv[1] ) == "--prepare-update";
    message["status"] = prepare ? "candidate_registered" : "available";
    if( mode == "idle" ) message["status"] = "up_to_date";
    if( mode == "unknown" ) message["status"] = "made-up-state";
    if( mode == "ready-lie" ) message["installationReady"] = true;
    if( mode == "missing-id" ) message.erase( "manifestSha256" );
    if( prepare )
    {
        message["directory"] = std::filesystem::path( executable ).parent_path().string();
        message["expectedTarget"] = "selections/fixture";
    }
    std::string line = message.dump() + "\n";
    if( mode == "chunked" )
    {
        for( const auto character : line )
            std::cout << character << std::flush;
    }
    else std::cout << line << std::flush;
    if( mode == "extra-terminal" ) std::cout << line << std::flush;
    return mode == "nonzero" ? 7 : 0;
}

struct Scenario
{
    wxString configuration = wxFileName::CreateTempFileName( "kicad-update-native-" );
    std::vector<nlohmann::json> messages;
    std::unique_ptr<AUTOMATION_UPDATE_CLIENT> client;
    explicit Scenario( const std::string& mode )
    {
        std::ofstream file( configuration.ToStdString() );
        file << mode;
        file.close();
        client = std::make_unique<AUTOMATION_UPDATE_CLIENT>( wxString::FromUTF8( executable ), configuration,
                    [this]( const auto& message )
                    {
                        messages.push_back( message );
                        BOOST_TEST_MESSAGE( "Fixture updater response: " << message.dump() );
                    } );
    }
    ~Scenario() { client.reset(); wxRemoveFile( configuration ); }
    size_t Count( const std::string& status ) const
    {
        return std::count_if( messages.begin(), messages.end(), [&]( const auto& item )
                            { return item.value( "status", "" ) == status; } );
    }
};

void awaitCondition( const std::function<bool()>& done, int timeout = 8000 )
{
    wxEventLoop loop;
    wxEventLoopActivator active( &loop );
    wxEvtHandler events;
    wxTimer timer( &events );
    wxStopWatch elapsed;
    bool timedOut = false;
    events.Bind( wxEVT_TIMER, [&]( wxTimerEvent& )
    {
        timedOut = elapsed.Time() >= timeout;
        if( done() || timedOut ) loop.Exit();
    } );
    timer.Start( 10 );
    loop.Run();
    timer.Stop();
    BOOST_REQUIRE_MESSAGE( !timedOut, "Native updater event-loop deadline exceeded" );
}
}

BOOST_AUTO_TEST_CASE( StartsPreparesAndDoesNotRepeatRegisteredCandidate )
{
    for( const std::string mode : { "success", "chunked" } )
    {
        BOOST_TEST_CONTEXT( "helper output mode=" << mode )
        {
        Scenario scenario( mode );
        scenario.client->Start( 3600000 );
        awaitCondition( [&] { return scenario.client->Candidate().is_object() && !scenario.client->IsRunning(); } );
        BOOST_CHECK_EQUAL( scenario.Count( "helper_started" ), 2 );
        scenario.client->Check();
        awaitCondition( [&] { return scenario.Count( "available" ) == 2 && !scenario.client->IsRunning(); } );
        BOOST_CHECK_EQUAL( scenario.Count( "helper_started" ), 3 );
        BOOST_CHECK_EQUAL( scenario.Count( "candidate_registered" ), 1 );
        BOOST_CHECK_EQUAL( scenario.Count( "failed" ), 0 );
        }
    }
}

BOOST_AUTO_TEST_CASE( ScheduledChecksAndFailuresAreRecoverable )
{
    Scenario idle( "idle" );
    idle.client->Start( 100 );
    awaitCondition( [&] { return idle.Count( "up_to_date" ) >= 2 && !idle.client->IsRunning(); } );
    idle.client.reset();
    for( const std::string mode : { "malformed", "no-terminal", "oversize", "unknown", "ready-lie",
                                   "missing-id", "nonzero", "extra-terminal" } )
    {
        Scenario scenario( mode );
        scenario.client->Check();
        awaitCondition( [&] { return scenario.Count( "failed" ) > 0 && !scenario.client->IsRunning(); } );
        BOOST_CHECK( scenario.client->Candidate().is_null() );
        { std::ofstream file( scenario.configuration.ToStdString() ); file << "idle"; }
        scenario.client->Check();
        awaitCondition( [&] { return scenario.Count( "up_to_date" ) > 0 && !scenario.client->IsRunning(); } );
    }
}

BOOST_AUTO_TEST_CASE( CancellationAndOwnerDestructionReapOnlyTheirChild )
{
    for( const std::string mode : { "hang", "ignore-interrupt" } )
    {
        Scenario scenario( mode );
        scenario.client->Check();
        awaitCondition( [&] { return scenario.Count( "checking" ) > 0; } );
        int pid = scenario.messages.back().at( "fixturePid" ).get<int>();
        scenario.client->Cancel();
        awaitCondition( [&] { return !scenario.client->IsRunning(); } );
        BOOST_CHECK( !wxProcess::Exists( pid ) );
        BOOST_CHECK_EQUAL( scenario.Count( "cancelled" ), 1 );
        Scenario destroyed( mode );
        destroyed.client->Check();
        awaitCondition( [&] { return destroyed.Count( "checking" ) > 0; } );
        pid = destroyed.messages.back().at( "fixturePid" ).get<int>();
        destroyed.client.reset();
        awaitCondition( [&] { return !wxProcess::Exists( pid ); } );
    }
}

BOOST_AUTO_TEST_CASE( RestartRequestCanCancelAndRetryWithoutLosingCandidate )
{
    Scenario scenario( "success" );
    wxString root = wxFileName::CreateTempFileName( "kicad-restart-request-" );
    wxRemoveFile( root );
    std::filesystem::create_directories( root.ToStdString() + "/state" );
    std::filesystem::create_directories( root.ToStdString() + "/manager" );
    auto select = [&]( bool refreshed )
    {
#ifdef __WXMSW__
        std::ofstream output( root.ToStdString() + "/manager/current.json" );
        output << nlohmann::json( { { "schemaVersion", 1 },
            { "selectionId", refreshed ? "33333333-3333-4333-8333-333333333333" : "22222222-2222-4222-8222-222222222222" },
            { "versionTarget", "versions/" + std::string( 64, 'b' ) + "/payload" } } ).dump();
#else
        std::filesystem::remove( root.ToStdString() + "/manager/current" );
        std::filesystem::create_symlink( refreshed ? "selections/refreshed" : "selections/fixture", root.ToStdString() + "/manager/current" );
#endif
    };
    select( false );
    struct Cleanup { std::string path; ~Cleanup() { std::filesystem::remove_all( path ); } } cleanup{ root.ToStdString() };
    std::string project = root.ToStdString() + "/fixture.kicad_pro";
    { std::ofstream output( project ); output << "{}"; }
    { std::ofstream config( scenario.configuration.ToStdString() );
      config << nlohmann::json( { { "installationRoot", root.ToStdString() }, { "fixtureMode", "success" } } ).dump(); }
    scenario.client->Check();
    awaitCondition( [&] { return scenario.client->Candidate().is_object() && !scenario.client->IsRunning(); } );
    BOOST_REQUIRE( scenario.client->Restart( wxString::FromUTF8( project ), "c9adf9b5-3070-43e4-90c1-c701123c6804", true ) );
    awaitCondition( [&] { return scenario.Count( "restart_waiting" ) == 1; } );
    std::vector<std::filesystem::path> requests;
    for( const auto& entry : std::filesystem::directory_iterator( root.ToStdString() + "/state" ) ) requests.push_back( entry.path() );
    BOOST_REQUIRE_EQUAL( requests.size(), 1 );
    std::ifstream requestFile( requests[0] );
    auto request = nlohmann::json::parse( requestFile ).at( "request" );
    BOOST_CHECK_EQUAL( request.at( "projectPath" ).get<std::string>(), project );
    BOOST_CHECK_EQUAL( request.at( "oldProcess" ).at( "processId" ).get<long>(), wxGetProcessId() );
#ifdef __WXMSW__
    BOOST_CHECK( request.at( "oldProcess" ).at( "creationFileTime" ).is_string() );
    BOOST_CHECK( std::stoull( request.at( "oldProcess" ).at( "creationFileTime" ).get<std::string>() ) > 0 );
#elif defined( __WXMAC__ )
    BOOST_CHECK( request.at( "oldProcess" ).at( "startSeconds" ).get<uint64_t>() > 0 );
#else
    BOOST_CHECK( request.at( "oldProcess" ).at( "startTicks" ).get<uint64_t>() > 0 );
#endif
    BOOST_CHECK_EQUAL( request.at( "instanceId" ).get<std::string>(), "c9adf9b5-3070-43e4-90c1-c701123c6804" );
    scenario.client->Cancel();
    awaitCondition( [&] { return !scenario.client->IsRunning(); } );
    BOOST_CHECK_EQUAL( scenario.Count( "cancelled" ), 1 );
    BOOST_CHECK( scenario.client->Candidate().is_object() );
    select( true );
    { std::ofstream mode( root.ToStdString() + "/restart-mode" ); mode << "reject"; }
    BOOST_REQUIRE( scenario.client->Restart( wxString::FromUTF8( project ), "c9adf9b5-3070-43e4-90c1-c701123c6804", true ) );
    awaitCondition( [&] { return scenario.Count( "failed" ) > 0 && !scenario.client->IsRunning(); } );
    BOOST_CHECK( scenario.client->Candidate().is_object() );
    bool refreshed = false;
    for( const auto& entry : std::filesystem::directory_iterator( root.ToStdString() + "/state" ) )
    {
        std::ifstream file( entry.path() );
#ifdef __WXMSW__
        if( nlohmann::json::parse( file ).at( "request" ).at( "expectedSelectionId" ) == "33333333-3333-4333-8333-333333333333" ) refreshed = true;
#else
        if( nlohmann::json::parse( file ).at( "request" ).at( "expectedTarget" ) == "selections/refreshed" ) refreshed = true;
#endif
    }
    BOOST_CHECK( refreshed );
}

bool initTests() { return true; }
int main( int argc, char** argv )
{
    executable = wxFileName( wxString::FromUTF8( argv[0] ) ).GetAbsolutePath().ToStdString();
    if( argc > 1 && ( std::string( argv[1] ) == "--check-update" || std::string( argv[1] ) == "--prepare-update"
                      || std::string( argv[1] ) == "--restart-update" ) )
        return fixture( argc, argv );
    wxApp::SetInstance( new wxApp );
    if( !wxInitialize( argc, argv ) ) return 2;
    wxLog::SetActiveTarget( new wxLogStderr );
    int result = boost::unit_test::unit_test_main( initTests, argc, argv );
    wxUninitialize();
    return result;
}
