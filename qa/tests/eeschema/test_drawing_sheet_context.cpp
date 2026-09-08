/*
 * Copyright The KiCad Developers, see AUTHORS.txt for contributors.
 * SPDX-License-Identifier: GPL-3.0-or-later
 */
#include <boost/test/unit_test.hpp>
#include <drawing_sheet/ds_data_item.h>
#include <drawing_sheet/ds_data_model.h>
#include <drawing_sheet/ds_draw_item.h>
#include <page_info.h>
#include <title_block.h>
#include <base_units.h>

BOOST_AUTO_TEST_SUITE( DrawingSheetContext )

BOOST_AUTO_TEST_CASE( PrivatePagesHaveIndependentUnitsMarginsAndCachedItems )
{
    DS_DATA_MODEL first;
    first.SetEmptyLayout();
    first.ClearList();
    first.AllowVoidList( true );
    first.SetRightMargin( 10 );
    auto* segment = new DS_DATA_ITEM( DS_DATA_ITEM::DS_SEGMENT );
    segment->SetStart( 10, 20 ); segment->SetEnd( 20, 20 );
    first.Append( segment );
    auto second = first.CloneForRendering();
    second->SetRightMargin( 30 );
    PAGE_INFO a4( PAGE_SIZE_TYPE::A4 ), a3( PAGE_SIZE_TYPE::A3 );
    TITLE_BLOCK title;
    DS_DRAW_ITEM_LIST firstDraw( schIUScale ), secondDraw( schIUScale );
    auto* global = &DS_DATA_MODEL::GetTheInstance();
    const auto originalUnits = global->m_WSunits2Iu;
    firstDraw.BuildDrawItemsList( a4, title, &first );
    const auto firstPosition = segment->GetStartPosIU();
    BOOST_REQUIRE( !segment->GetDrawItems().empty() );
    auto* cachedItem = segment->GetDrawItems().front();
    secondDraw.BuildDrawItemsList( a3, title, second.get() );
    BOOST_CHECK( firstPosition != second->GetItem( 0 )->GetStartPosIU() );
    BOOST_CHECK( segment->GetStartPosIU() == firstPosition );
    BOOST_CHECK( segment->GetDrawItems().front() == cachedItem );
    BOOST_CHECK( &DS_DATA_MODEL::GetTheInstance() == global );
    BOOST_CHECK_EQUAL( global->m_WSunits2Iu, originalUnits );
    firstDraw.BuildDrawItemsList( a4, title, &first );
    BOOST_CHECK( segment->GetStartPosIU() == firstPosition );
}

BOOST_AUTO_TEST_CASE( DeliberatelyEmptyLayoutStaysEmptyWhenClonedAndRendered )
{
    DS_DATA_MODEL source;
    source.ClearList();
    source.AllowVoidList( true );
    auto copy = source.CloneForRendering();
    BOOST_REQUIRE( copy->VoidListAllowed() );
    DS_DRAW_ITEM_LIST items( schIUScale );
    PAGE_INFO page( PAGE_SIZE_TYPE::A4 );
    TITLE_BLOCK title;
    items.BuildDrawItemsList( page, title, copy.get() );
    BOOST_CHECK_EQUAL( copy->GetCount(), 0 );
    BOOST_CHECK( items.GetFirst() == nullptr );
}

BOOST_AUTO_TEST_CASE( NativeEmptyLayoutPreservesItsCompatibilityContent )
{
    DS_DATA_MODEL source;
    source.SetEmptyLayout();
    auto copy = source.CloneForRendering();
    BOOST_CHECK_EQUAL( copy->GetCount(), source.GetCount() );
    wxString original, cloned;
    source.SaveInString( &original );
    copy->SaveInString( &cloned );
    BOOST_CHECK( original == cloned );
}

BOOST_AUTO_TEST_CASE( PrivateTitleBlocksResolveTheirOwnSheetAndVariantContext )
{
    DS_DATA_MODEL source;
    source.AllowVoidList( true );
    auto* text = new DS_DATA_ITEM_TEXT( wxS( "${TITLE}|${SHEETNAME}|${SHEETPATH}|${#}/${##}|${VARIANT}" ) );
    text->SetStart( 10, 10, LT_CORNER );
    source.Append( text );
    auto* firstOnly = new DS_DATA_ITEM_TEXT( wxS( "First page only" ) );
    firstOnly->SetStart( 10, 15, LT_CORNER );
    firstOnly->SetPage1Option( FIRST_PAGE_ONLY );
    source.Append( firstOnly );
    auto other = source.CloneForRendering();
    PAGE_INFO page( PAGE_SIZE_TYPE::A4 );
    TITLE_BLOCK power, control;
    power.SetTitle( wxS( "Power" ) ); control.SetTitle( wxS( "Control" ) );
    DS_DRAW_ITEM_LIST first( schIUScale ), second( schIUScale );
    first.SetSheetName( wxS( "Supply" ) ); first.SetSheetPath( wxS( "/Supply" ) );
    first.SetPageNumber( wxS( "1" ) ); first.SetSheetCount( 2 );
    first.SetVariantName( wxS( "A" ) ); first.SetIsFirstPage( true );
    first.BuildDrawItemsList( page, power, &source );
    auto* firstText = dynamic_cast<DS_DRAW_ITEM_TEXT*>( first.GetFirst() );
    BOOST_REQUIRE( firstText );
    BOOST_CHECK( firstText->GetText() == wxS( "Power|Supply|/Supply|1/2|A" ) );
    BOOST_CHECK( first.GetNext() != nullptr );
    second.SetSheetName( wxS( "Logic" ) ); second.SetSheetPath( wxS( "/Logic" ) );
    second.SetPageNumber( wxS( "2" ) ); second.SetSheetCount( 2 );
    second.SetVariantName( wxS( "B" ) ); second.SetIsFirstPage( false );
    second.BuildDrawItemsList( page, control, other.get() );
    auto* secondText = dynamic_cast<DS_DRAW_ITEM_TEXT*>( second.GetFirst() );
    BOOST_REQUIRE( secondText );
    BOOST_CHECK( secondText->GetText() == wxS( "Control|Logic|/Logic|2/2|B" ) );
    BOOST_CHECK( second.GetNext() == nullptr );
    BOOST_CHECK( firstText->GetText() == wxS( "Power|Supply|/Supply|1/2|A" ) );
}

BOOST_AUTO_TEST_SUITE_END()
