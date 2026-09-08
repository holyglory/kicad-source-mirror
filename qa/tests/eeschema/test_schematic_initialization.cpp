/* Copyright The KiCad Developers. SPDX-License-Identifier: GPL-3.0-or-later */
#include <boost/test/unit_test.hpp>
#include <schematic.h>
#include <sch_screen.h>
#include <sch_sheet.h>
#include <sch_sheet_path.h>

BOOST_AUTO_TEST_SUITE( SchematicInitialization )

BOOST_AUTO_TEST_CASE( DeclaredRootIdentityPrecedesSheetPathConstruction )
{
    SCHEMATIC schematic( nullptr );
    const KIID declared;
    schematic.CreateDefaultScreens( declared );
    BOOST_REQUIRE( schematic.GetTopLevelSheets().size() == 1 );
    BOOST_CHECK( schematic.GetTopLevelSheet()->m_Uuid == declared );
    BOOST_CHECK( schematic.RootScreen()->GetUuid() == declared );
    BOOST_REQUIRE( schematic.Hierarchy().size() == 1 );
    BOOST_CHECK( schematic.Hierarchy().at( 0 ).Last()->m_Uuid == declared );
    BOOST_CHECK( schematic.Hierarchy().at( 0 ).GetPageNumber() == wxS( "1" ) );

    schematic.CreateDefaultScreens();
    BOOST_CHECK( schematic.RootScreen()->GetUuid() != niluuid );
    BOOST_CHECK( schematic.RootScreen()->GetUuid() != declared );
    BOOST_CHECK( schematic.GetTopLevelSheet()->m_Uuid == schematic.RootScreen()->GetUuid() );
    BOOST_CHECK( schematic.Hierarchy().at( 0 ).GetPageNumber() == wxS( "1" ) );
}

BOOST_AUTO_TEST_SUITE_END()
