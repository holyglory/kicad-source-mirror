/* KiCad local change notifications. GPL-3.0-or-later. */
#include <kinng_publisher.h>
#include <nng/nng.h>
#include <nng/protocol/pubsub0/pub.h>
#include <chrono>
#include <condition_variable>
#include <mutex>
#include <thread>

struct KINNG_PUBLISHER::IMPL
{
    explicit IMPL( const std::string& aUrl ) : url( aUrl ) {}
    std::string url;
    std::string error;
    std::string heartbeat;
    nng_socket socket = NNG_SOCKET_INITIALIZER;
    bool running = false;
    bool stopping = false;
    std::mutex mutex;
    std::condition_variable wake;
    std::thread thread;

    bool Send( const std::string& aMessage )
    {
        return nng_send( socket, const_cast<char*>( aMessage.data() ), aMessage.size(),
                         NNG_FLAG_NONBLOCK ) == 0;
    }
};

KINNG_PUBLISHER::KINNG_PUBLISHER( const std::string& aUrl ) : m_impl( new IMPL( aUrl ) ) {}
KINNG_PUBLISHER::~KINNG_PUBLISHER() { Stop(); }

bool KINNG_PUBLISHER::Start( const std::string& aHeartbeat )
{
    auto& state = *m_impl;
    if( state.running ) return true;
    int error = nng_pub0_open( &state.socket );
    if( error == 0 )
    {
        error = nng_listen( state.socket, state.url.c_str(), nullptr, 0 );
        if( error != 0 ) nng_close( state.socket );
    }
    if( error != 0 )
    {
        state.error = nng_strerror( error );
        return false;
    }
    state.heartbeat = aHeartbeat;
    state.stopping = false;
    state.running = true;
    state.error.clear();
    try
    {
        state.thread = std::thread( [&state]()
        {
            std::unique_lock<std::mutex> lock( state.mutex );
            while( !state.wake.wait_for( lock, std::chrono::seconds( 1 ),
                                        [&state]() { return state.stopping; } ) )
                state.Send( state.heartbeat );
        } );
    }
    catch( ... )
    {
        state.running = false;
        nng_close( state.socket );
        throw;
    }
    return true;
}

void KINNG_PUBLISHER::Stop()
{
    auto& state = *m_impl;
    if( !state.running ) return;
    {
        std::lock_guard<std::mutex> lock( state.mutex );
        state.stopping = true;
        state.wake.notify_all();
    }
    state.thread.join();
    nng_close( state.socket );
    state.running = false;
}

bool KINNG_PUBLISHER::Publish( const std::string& aMessage, const std::string& aHeartbeat )
{
    auto& state = *m_impl;
    std::lock_guard<std::mutex> lock( state.mutex );
    if( !state.running || state.stopping ) return false;
    state.heartbeat = aHeartbeat;
    return state.Send( aMessage );
}

const std::string& KINNG_PUBLISHER::LastError() const { return m_impl->error; }
