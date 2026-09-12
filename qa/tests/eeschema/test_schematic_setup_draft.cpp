/* Native Schematic Setup isolation. GPL-3.0-or-later. */
#include <boost/test/unit_test.hpp>
#include <sch_setup_draft.h>
#include <project/net_settings.h>
#include <refdes_tracker.h>
#include <settings/json_settings_internals.h>

BOOST_AUTO_TEST_CASE( SchematicDraftDoesNotChangeLiveProjectOrReferenceTracker )
{
    PROJECT_FILE project( "detached-fixture.kicad_pro" );
    ERC_SETTINGS erc( &project, "erc" );
    SCHEMATIC_SETTINGS schematic( &project, "schematic" );
    project.m_ErcSettings = &erc; project.m_SchematicSettings = &schematic;
    project.Load(); erc.Load(); schematic.Load();
    project.m_TextVars["LOCATION"] = "cooling side";
    project.NetSettings()->SetNetChainClassDefinitions( { "Unassigned" } );
    const auto before = project.CaptureCurrentState();
    const auto stored = static_cast<const nlohmann::json&>( *project.Internals() );
    {
        SCH_SETUP_DRAFT draft( project );
        BOOST_CHECK( !draft.Changed() && draft.MatchesLive( project ) );
        BOOST_REQUIRE( draft.SchematicSettings().m_refDesTracker );
        BOOST_CHECK( draft.SchematicSettings().m_refDesTracker != schematic.m_refDesTracker );
        draft.SchematicSettings().m_refDesTracker->SetReuseRefDes( !schematic.m_refDesTracker->GetReuseRefDes() );
        draft.SchematicSettings().m_DefaultTextSize += 1;
        draft.ProjectSettings().m_TextVars["LOCATION"] = "pending";
        draft.ProjectSettings().NetSettings()->SetNetChainClassDefinitions( { "Pending" } );
        draft.ErcSettings().SetPinMapValue( 0, 0, PIN_ERROR::PP_ERROR );
        BOOST_CHECK( draft.Changed() );
        BOOST_CHECK( project.CaptureCurrentState() == before );
        project.m_TextVars["INTERVENING"] = "external edit";
        BOOST_CHECK( !draft.MatchesLive( project ) );
        project.m_TextVars.erase( "INTERVENING" );
    }
    BOOST_CHECK( project.CaptureCurrentState() == before );
    BOOST_CHECK( static_cast<const nlohmann::json&>( *project.Internals() ) == stored );
}
