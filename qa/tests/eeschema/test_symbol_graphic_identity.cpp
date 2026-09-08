/*
 * This program source code file is part of KiCad, a free EDA CAD application.
 * Copyright The KiCad Developers, see AUTHORS.txt for contributors.
 * SPDX-License-Identifier: GPL-3.0-or-later
 */

#include <boost/test/unit_test.hpp>
#include <google/protobuf/any.pb.h>
#include <lib_symbol.h>
#include <sch_io/kicad_sexpr/sch_io_kicad_sexpr.h>
#include <sch_text.h>
#include <wx/filename.h>

#include <fstream>
#include <map>

namespace
{
struct TEMP_IDENTITY_LIBRARY
{
    wxString path = wxFileName::CreateTempFileName( wxS( "kicad_symbol_identity_" ) );
    ~TEMP_IDENTITY_LIBRARY() { wxRemoveFile( path ); }
};

std::map<std::string, std::string> snapshotGraphics( LIB_SYMBOL& aSymbol )
{
    std::map<std::string, std::string> result;

    for( const SCH_ITEM& item : aSymbol.GetDrawItems() )
    {
        if( item.Type() == SCH_FIELD_T )
            continue;

        google::protobuf::Any value;
        item.Serialize( value );
        BOOST_REQUIRE( result.emplace( item.m_Uuid.AsStdString(), value.SerializeAsString() ).second );
    }

    return result;
}
}

BOOST_AUTO_TEST_SUITE( SymbolGraphicIdentity )

BOOST_AUTO_TEST_CASE( LegacyLibraryGraphicsAndPinsKeepIdentityThroughSaveEditAndReload )
{
    TEMP_IDENTITY_LIBRARY library;
    // Deliberately omit UUIDs: legacy files acquire identities on their first
    // load. Two geometrically identical circles must not share an identity.
    {
        std::ofstream file( library.path.ToStdString() );
        file << R"((kicad_symbol_lib (version 20260830) (generator "identity_test")
          (symbol "Identity" (in_bom yes) (on_board yes)
            (property "Reference" "U" (at 0 0 0) (effects (font (size 1.27 1.27))))
            (property "Value" "Identity" (at 0 0 0) (effects (font (size 1.27 1.27))))
            (symbol "Identity_0_1"
              (pin passive line (at 0 0 0) (length 2.54)
                (name "Same" (effects (font (size 1.27 1.27))))
                (number "1" (effects (font (size 1.27 1.27)))))
              (pin passive line (at 0 0 0) (length 2.54)
                (name "Same" (effects (font (size 1.27 1.27))))
                (number "1" (effects (font (size 1.27 1.27)))))
              (circle (center 0 2) (radius 1) (stroke (width 0) (type default)) (fill (type none)))
              (circle (center 0 2) (radius 1) (stroke (width 0) (type default)) (fill (type none)))
              (arc (start 2 1) (mid 3 2) (end 4 1) (stroke (width 0) (type default)) (fill (type none)))
              (rectangle (start 2 3) (end 4 4) (stroke (width 0) (type default)) (fill (type none)))
              (bezier (pts (xy 2 5) (xy 3 6) (xy 4 4) (xy 5 5))
                (stroke (width 0) (type default)) (fill (type none)))
              (polyline (pts (xy 2 7) (xy 3 8) (xy 4 7))
                (stroke (width 0) (type default)) (fill (type none)))
              (ellipse (center 6 2) (major_radius 1) (minor_radius 0.5) (rotation_angle 0)
                (stroke (width 0) (type default)) (fill (type none)))
              (ellipse_arc (center 6 4) (major_radius 1) (minor_radius 0.5)
                (rotation_angle 0) (start_angle 0) (end_angle 90)
                (stroke (width 0) (type default)) (fill (type none)))
              (text "Definition note" (at 6 6 0) (effects (font (size 1 1))))
              (text_box "Definition box" (at 6 8 0) (size 4 2) (margins 0 0 0 0)
                (stroke (width 0) (type default)) (fill (type none)) (effects (font (size 1 1))))))))";
        BOOST_REQUIRE( file.good() );
    }

    std::map<std::string, std::string> expected;
    std::string textId;

    for( int pass = 0; pass < 3; ++pass )
    {
        // Fresh IO object prevents the library cache from masking a reload bug.
        SCH_IO_KICAD_SEXPR io;
        LIB_SYMBOL* symbol = io.LoadSymbol( library.path, wxS( "Identity" ) );
        BOOST_REQUIRE( symbol );
        auto observed = snapshotGraphics( *symbol );
        BOOST_REQUIRE_EQUAL( observed.size(), 12 );

        if( pass != 0 )
            BOOST_CHECK_MESSAGE( observed == expected, "Graphic identities and properties changed on library reload" );

        for( SCH_ITEM& item : symbol->GetDrawItems() )
        {
            if( item.Type() != SCH_TEXT_T )
                continue;

            if( pass == 0 )
                textId = item.m_Uuid.AsStdString();
            else
                BOOST_CHECK_EQUAL( item.m_Uuid.AsStdString(), textId );

            if( pass == 1 )
                static_cast<SCH_TEXT&>( item ).SetText( wxS( "Edited by persistent identity" ) );
        }

        expected = snapshotGraphics( *symbol );
        io.SaveSymbol( library.path, new LIB_SYMBOL( *symbol ) );
        io.SaveLibrary( library.path );
    }
}

BOOST_AUTO_TEST_SUITE_END()
