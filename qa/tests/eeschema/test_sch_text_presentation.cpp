/*
 * Copyright The KiCad Developers, see AUTHORS.txt for contributors.
 * SPDX-License-Identifier: GPL-3.0-or-later
 */
#include <boost/test/unit_test.hpp>
#include <api/sch_text_presentation.h>
#include <geometry/shape_compound.h>
#include <lib_symbol.h>
#include <sch_field.h>
#include <sch_sheet.h>
#include <sch_symbol.h>
#include <qa_utils/wx_utils/unit_test_utils.h>
#include <wx/filename.h>
#include <wx/image.h>
#include <gal/cairo/cairo_print.h>

BOOST_AUTO_TEST_SUITE( SchematicTextPresentation )

BOOST_AUTO_TEST_CASE( StrokeBoundsIncludePainterOffsetAndRotation )
{
    SCH_RENDER_SETTINGS settings;
    SCH_TEXT text( VECTOR2I( 450000, 550000 ), wxS( "~{CLOCK}" ) );
    text.SetTextSize( VECTOR2I( 12700, 12700 ) );
    text.SetTextThickness( 600 );
    for( const auto angle : { ANGLE_0, ANGLE_90 } )
    {
        text.SetTextAngle( angle );
        auto glyphs = text.GetEffectiveTextShape( false );
        BOOST_REQUIRE( !glyphs->Shapes().empty() );
        BOX2I expected = glyphs->BBox();
        expected.Move( text.GetSchematicTextOffset( &settings ) );
        auto actual = SchTextPresentationBounds( text, settings );
        BOOST_REQUIRE( actual );
        BOOST_CHECK( *actual == expected );
        BOOST_CHECK( *actual != glyphs->BBox() );
    }
}

BOOST_AUTO_TEST_CASE( StrokeBoundsUseRendererDefaultPenAndExcludeUnsupportedKinds )
{
    SCH_RENDER_SETTINGS settings;
    SCH_TEXT text( VECTOR2I( 450000, 550000 ), wxS( "CLOCK" ) );
    text.SetTextSize( VECTOR2I( 12700, 12700 ) );
    text.SetTextThickness( 0 );
    settings.SetDefaultPenWidth( 100 );
    auto thin = SchTextPresentationBounds( text, settings );
    settings.SetDefaultPenWidth( 1000 );
    auto thick = SchTextPresentationBounds( text, settings );
    BOOST_REQUIRE( thin );
    BOOST_REQUIRE( thick );
    BOOST_CHECK_GT( thick->GetWidth(), thin->GetWidth() );
    BOOST_CHECK_GT( thick->GetHeight(), thin->GetHeight() );
    text.SetText( wxEmptyString );
    BOOST_CHECK( !SchTextPresentationBounds( text, settings ) );
    text.SetText( wxS( "Device text" ) );
    text.SetLayer( LAYER_DEVICE );
    BOOST_CHECK( !SchTextPresentationBounds( text, settings ) );
}

BOOST_AUTO_TEST_CASE( OutlineBoundsIncludeNativePainterOffsetsAndRotation )
{
    wxFileName file( KI_TEST::GetTestDataRootDir() );
    file.RemoveLastDir();
    file.AppendDir( wxS( "resources" ) );
    file.AppendDir( wxS( "fonts" ) );
    file.SetFullName( wxS( "NotoSans-Regular.ttf" ) );
    BOOST_REQUIRE( file.FileExists() );
    std::vector<wxString> embeddedFonts{ file.GetFullPath() };
    auto* font = KIFONT::FONT::GetFont( wxS( "Noto Sans" ), false, false, &embeddedFonts );
    BOOST_REQUIRE( font && font->IsOutline() );
    SCH_RENDER_SETTINGS settings;
    SCH_TEXT text( VECTOR2I( 450000, 550000 ), wxS( "~{CLOCK}" ) );
    text.SetFont( font );
    text.SetTextSize( VECTOR2I( 12700, 12700 ) );
    text.SetTextThickness( 600 );
    for( const auto angle : { ANGLE_0, ANGLE_90 } )
    {
        text.SetTextAngle( angle );
        auto glyphs = text.GetEffectiveTextShape( false );
        BOOST_REQUIRE( !glyphs->Shapes().empty() );
        BOX2I expected = glyphs->BBox();
        expected.Move( text.GetSchematicTextOffset( &settings )
                       + text.GetOffsetToMatchSCH_FIELD( nullptr, text.GetShownText( true ) ) );
        auto actual = SchTextPresentationBounds( text, settings );
        BOOST_REQUIRE( actual );
        BOOST_CHECK( *actual == expected );
        BOOST_CHECK( *actual != glyphs->BBox() );
    }
    text.SetText( wxEmptyString );
    BOOST_CHECK( !SchTextPresentationBounds( text, settings ) );
}

BOOST_AUTO_TEST_CASE( OutlineMeasurementsEncloseActualNativePainterPixels )
{
    wxFileName file( KI_TEST::GetTestDataRootDir() );
    file.RemoveLastDir(); file.AppendDir( wxS( "resources" ) ); file.AppendDir( wxS( "fonts" ) );
    file.SetFullName( wxS( "NotoSans-Regular.ttf" ) );
    BOOST_REQUIRE( file.FileExists() );
    std::vector<wxString> embeddedFonts{ file.GetFullPath() };
    auto* font = KIFONT::FONT::GetFont( wxS( "Noto Sans" ), false, false, &embeddedFonts );
    BOOST_REQUIRE( font && font->IsOutline() );
    SCH_TEXT text( VECTOR2I( 450000, 550000 ), wxS( "~{CLOCK}" ) );
    text.SetFont( font );
    text.SetTextSize( VECTOR2I( 12700, 12700 ) );
    text.SetTextThickness( 600 );
    text.SetTextColor( KIGFX::COLOR4D( 0, 0, 0, 1 ) );
    for( const wxString& content : { wxString( wxS( "CLOCK" ) ), wxString( wxS( "~{CLOCK}" ) ),
                                    wxString( wxS( "A~{B}C" ) ) } )
    for( const auto angle : { ANGLE_0, ANGLE_90 } )
    {
        text.SetText( content );
        text.SetTextAngle( angle );
        wxImage image( 512, 512 ); image.InitAlpha();
        KIGFX::GAL_DISPLAY_OPTIONS options;
        VECTOR2D expectedStart, expectedEnd;
        {
            auto gal = KIGFX::CAIRO_PRINT_GAL::Create( options, &image, 96.0 );
            gal->SetWorldUnitLength( 1.0 / ( 25.4 * schIUScale.IU_PER_MM ) );
            gal->SetScreenSize( VECTOR2I( 512, 512 ) );
            gal->SetNativePaperSize( VECTOR2D( 512.0 / 96.0, 512.0 / 96.0 ), true );
            gal->SetZoomFactor( 25400000.0 / ( 96.0 * 20000.0 ) );
            gal->SetLookAtPoint( text.GetPosition() );
            gal->SetClearColor( KIGFX::COLOR4D( 1, 1, 1, 1 ) );
            KIGFX::SCH_PAINTER painter( gal.get() );
            painter.GetSettings()->SetIsPrinting( true );
            auto bounds = SchTextPresentationBounds( text, *painter.GetSettings() );
            BOOST_REQUIRE( bounds );
            gal->BeginDrawing();
            expectedStart = gal->GetWorldScreenMatrix() * VECTOR2D( bounds->GetOrigin() );
            expectedEnd = gal->GetWorldScreenMatrix() * VECTOR2D( bounds->GetEnd() );
            painter.Draw( &text, LAYER_NOTES );
            gal->EndDrawing();
        }
        int left = 512, top = 512, right = -1, bottom = -1;
        for( int y = 0; y < 512; ++y )
            for( int x = 0; x < 512; ++x )
                if( image.GetRed( x, y ) < 250 || image.GetGreen( x, y ) < 250 || image.GetBlue( x, y ) < 250 )
                {
                    left = std::min( left, x ); right = std::max( right, x );
                    top = std::min( top, y ); bottom = std::max( bottom, y );
                }
        BOOST_REQUIRE_GE( right, 0 );
        // Permit at most two raster pixels for antialiasing/edge quantization,
        // while rejecting offsets or padded text boxes that merely contain ink.
        BOOST_CHECK_SMALL( left - expectedStart.x, 2.0 );
        BOOST_CHECK_SMALL( top - expectedStart.y, 2.0 );
        BOOST_CHECK_SMALL( right + 1 - expectedEnd.x, 2.0 );
        BOOST_CHECK_SMALL( bottom + 1 - expectedEnd.y, 2.0 );
    }
}

BOOST_AUTO_TEST_SUITE_END()

BOOST_AUTO_TEST_SUITE( SchematicExplicitTextContext )

BOOST_AUTO_TEST_CASE( ExplicitTextMeasurementDoesNotContaminateDisplayedTextCache )
{
    SCH_TEXT text( VECTOR2I( 450000, 550000 ), wxS( "Visible text" ) );
    text.SetTextSize( VECTOR2I( 12700, 12700 ) );
    text.SetTextThickness( 600 );
    text.SetMultilineAllowed( true );
    const BOX2I cached = text.GetTextBox( nullptr );
    BOOST_CHECK( text.GetTextBoxForText( nullptr, text.GetShownText( true ) ) == cached );
    for( const wxString& value : { wxString( wxS( "X" ) ), wxString( wxS( "A much longer instance name\nSecond line" ) ) } )
    {
        SCH_TEXT literal( text );
        literal.SetText( value );
        BOOST_CHECK( text.GetTextBoxForText( nullptr, value ) == literal.GetTextBox( nullptr ) );
        BOOST_CHECK( text.GetTextBoxForText( nullptr, value, 0 ) == literal.GetTextBox( nullptr, 0 ) );
        BOOST_CHECK( text.GetTextBox( nullptr ) == cached );
        BOOST_CHECK( text.GetText() == wxS( "Visible text" ) );
    }
}

BOOST_AUTO_TEST_CASE( RepeatedSheetReferenceGeometryUsesRequestedInstance )
{
    SCH_SHEET firstSheet, secondSheet;
    SCH_SHEET_PATH first, second;
    first.push_back( &firstSheet );
    second.push_back( &secondSheet );
    LIB_SYMBOL library( wxS( "ContextFixture" ) );
    SCH_SYMBOL symbol( library, library.GetLibId(), nullptr, 1, 0, VECTOR2I( 450000, 550000 ) );
    SCH_SYMBOL literal( library, library.GetLibId(), nullptr, 1, 0, symbol.GetPosition() );
    symbol.SetRef( &first, wxS( "U1" ) );
    symbol.SetRef( &second, wxS( "U123456" ) );
    auto* field = symbol.GetField( FIELD_T::REFERENCE );
    auto* literalField = literal.GetField( FIELD_T::REFERENCE );
    field->SetText( wxS( "Visible reference" ) );
    field->SetTextSize( VECTOR2I( 12700, 12700 ) );
    literalField->SetTextSize( field->GetTextSize() );
    for( const int orientation : { SYM_ORIENT_0, SYM_ORIENT_90, SYM_MIRROR_X, SYM_MIRROR_Y } )
    {
        symbol.SetOrientation( orientation );
        literal.SetOrientation( orientation );
        const BOX2I cached = field->GetBoundingBox();
        for( const SCH_SHEET_PATH* path : { &first, &second, &first } )
        {
            literalField->SetText( field->GetShownText( path, true ) );
            BOOST_CHECK( field->GetBoundingBox( path, wxEmptyString ) == literalField->GetBoundingBox() );
            BOOST_CHECK( field->GetBoundingBox() == cached );
        }
        BOOST_CHECK( field->GetBoundingBox( &first, wxEmptyString )
                     != field->GetBoundingBox( &second, wxEmptyString ) );
        BOOST_CHECK( field->GetText() == wxS( "Visible reference" ) );
    }
}

BOOST_AUTO_TEST_SUITE_END()
