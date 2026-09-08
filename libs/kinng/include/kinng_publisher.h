/* KiCad local change notifications. GPL-3.0-or-later. */
#ifndef KICAD_KINNG_PUBLISHER_H
#define KICAD_KINNG_PUBLISHER_H

#include <memory>
#include <string>

// One native publisher, with a lightweight latest-cursor heartbeat. Start and
// Stop are owner-thread operations. Publishing never waits for a subscriber.
// Delivery is best effort: consumers must detect gaps and recover state.
class KINNG_PUBLISHER
{
public:
    explicit KINNG_PUBLISHER( const std::string& aUrl );
    ~KINNG_PUBLISHER();
    bool Start( const std::string& aHeartbeat );
    void Stop();
    bool Publish( const std::string& aMessage, const std::string& aHeartbeat );
    const std::string& LastError() const;

private:
    struct IMPL;
    std::unique_ptr<IMPL> m_impl;
};

#endif
