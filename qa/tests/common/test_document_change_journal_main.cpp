/* Focused platform-independent Boost.Test entry point. GPL-3.0-or-later. */
#include <boost/test/unit_test.hpp>

static bool InitJournalTests() { return true; }

int main( int argc, char** argv )
{
    return boost::unit_test::unit_test_main( &InitJournalTests, argc, argv );
}
