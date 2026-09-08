/* Native event publisher transport acceptance. GPL-3.0-or-later. */
#include <boost/test/unit_test.hpp>
#include <kinng_publisher.h>
#include <kiid.h>
#include <nng/nng.h>
#include <nng/protocol/pubsub0/sub.h>
#include <chrono>
#include <filesystem>

namespace
{
struct SUBSCRIBER
{
    nng_socket socket = NNG_SOCKET_INITIALIZER;
    explicit SUBSCRIBER( const std::string& url )
    {
        BOOST_REQUIRE_EQUAL( nng_sub0_open( &socket ), 0 );
        BOOST_REQUIRE_EQUAL( nng_socket_set( socket, NNG_OPT_SUB_SUBSCRIBE, nullptr, 0 ), 0 );
        BOOST_REQUIRE_EQUAL( nng_socket_set_ms( socket, NNG_OPT_RECVTIMEO, 2500 ), 0 );
        BOOST_REQUIRE_EQUAL( nng_dial( socket, url.c_str(), nullptr, 0 ), 0 );
    }
    ~SUBSCRIBER() { nng_close( socket ); }
    std::string Read()
    {
        char* data = nullptr; size_t size = 0;
        BOOST_REQUIRE_EQUAL( nng_recv( socket, &data, &size, NNG_FLAG_ALLOC ), 0 );
        std::string result( data, size ); nng_free( data, size ); return result;
    }
    std::string ReadPast( const std::string& oldHeartbeat )
    {
        auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds( 5 );
        while( true )
        {
            BOOST_REQUIRE( std::chrono::steady_clock::now() < deadline );
            std::string message = Read();
            if( message != oldHeartbeat ) return message;
        }
    }
};
}

BOOST_AUTO_TEST_SUITE( NativePublisher )

static void CheckPublisherJourney( const std::string& endpoint )
{
    KINNG_PUBLISHER publisher( endpoint );
    BOOST_REQUIRE( publisher.Start( "head-0" ) );
    SUBSCRIBER first( endpoint ), second( endpoint );
    BOOST_CHECK_EQUAL( first.Read(), "head-0" );
    BOOST_CHECK_EQUAL( second.Read(), "head-0" );
    KINNG_PUBLISHER collision( endpoint );
    BOOST_CHECK( !collision.Start( "wrong-head" ) );
    BOOST_CHECK( !collision.LastError().empty() );
    collision.Stop();
    BOOST_REQUIRE( publisher.Publish( "change-1", "head-1" ) );
    BOOST_CHECK_EQUAL( first.ReadPast( "head-0" ), "change-1" );
    BOOST_CHECK_EQUAL( second.ReadPast( "head-0" ), "change-1" );
    BOOST_CHECK_EQUAL( first.Read(), "head-1" );
    publisher.Stop();
    BOOST_CHECK( !publisher.Publish( "closed", "closed" ) );
    BOOST_REQUIRE( collision.Start( "replacement-head" ) );
    SUBSCRIBER replacement( endpoint );
    BOOST_CHECK_EQUAL( replacement.Read(), "replacement-head" );
    collision.Stop();
    collision.Stop();
}

BOOST_AUTO_TEST_CASE( BroadcastHeartbeatCollisionAndStopRecovery )
{
    CheckPublisherJourney( "inproc://kicad-events-" + KIID().AsStdString() );
}

BOOST_AUTO_TEST_CASE( LocalSocketCollisionAndStopRecovery )
{
    auto path = std::filesystem::temp_directory_path() / KIID().AsStdString().substr( 0, 8 );
    BOOST_REQUIRE( !std::filesystem::exists( path ) );
    CheckPublisherJourney( "ipc://" + path.string() );
    BOOST_CHECK( !std::filesystem::exists( path ) );
}

BOOST_AUTO_TEST_CASE( FailedStartupDoesNotCreateAPublisherOrPreventCleanup )
{
    KINNG_PUBLISHER publisher( "unsupported://native-fixture" );
    BOOST_CHECK( !publisher.Start( "head" ) );
    BOOST_CHECK( !publisher.LastError().empty() );
    BOOST_CHECK( !publisher.Publish( "change", "head" ) );
    publisher.Stop();
    BOOST_CHECK( !publisher.Start( "head" ) );
}

BOOST_AUTO_TEST_SUITE_END()
