/* Copyright The KiCad Developers. SPDX-License-Identifier: GPL-3.0-or-later */
#include <boost/test/unit_test.hpp>
#include <google/protobuf/any.pb.h>
#include <google/protobuf/unknown_field_set.h>
#include <lib_symbol.h>
#include <sch_symbol.h>
#include <schematic/schematic_types.pb.h>

BOOST_AUTO_TEST_SUITE( SchematicSymbolLibraryIdentity )

BOOST_AUTO_TEST_CASE( OriginalLibraryLinkOwnedDefinitionAndCacheAliasRoundTripIndependently )
{
    const LIB_ID sourceId( wxS( "Catalog" ), wxS( "Original" ) );
    const LIB_ID definitionId( wxS( "Local" ), wxS( "Edited" ) );
    LIB_SYMBOL library( wxS( "Edited" ) );
    library.SetLibId( definitionId );
    library.SetPinNameOffset( 0 );
    SCH_SYMBOL source;
    source.SetLibId( sourceId );
    source.SetLibSymbol( new LIB_SYMBOL( library ) );
    source.SetPinNameOffset( 1234 );
    source.SetSchSymbolLibraryName( wxS( "ScreenCopy_3" ) );
    google::protobuf::Any encoded;
    source.Serialize( encoded );
    kiapi::schematic::types::SchematicSymbolInstance message;
    BOOST_REQUIRE( encoded.UnpackTo( &message ) );
    BOOST_CHECK_EQUAL( message.library_id().library_nickname(), "Catalog" );
    BOOST_CHECK_EQUAL( message.library_id().entry_name(), "Original" );
    BOOST_CHECK_EQUAL( message.definition().id().library_nickname(), "Local" );
    BOOST_CHECK_EQUAL( message.definition().id().entry_name(), "Edited" );
    BOOST_CHECK_EQUAL( message.lib_name(), "ScreenCopy_3" );
    BOOST_REQUIRE( message.has_definition_pin_name_offset() );
    BOOST_CHECK_EQUAL( message.definition_pin_name_offset().value_nm(), 0 );

    SCH_SYMBOL restored;
    BOOST_REQUIRE( restored.Deserialize( encoded ) );
    BOOST_CHECK_EQUAL( restored.GetLibId().Format(), sourceId.Format() );
    BOOST_CHECK_EQUAL( restored.GetLibSymbolRef()->GetLibId().Format(), definitionId.Format() );
    // Boost's narrow diagnostic stream cannot print wchar_t arrays on libc++.
    // Compare printable UTF-8 values while retaining the exact alias check.
    BOOST_CHECK_EQUAL( restored.GetSchSymbolLibraryName().ToStdString( wxConvUTF8 ),
                       "ScreenCopy_3" );
    BOOST_CHECK_EQUAL( restored.GetLibSymbolRef()->GetPinNameOffset(), 0 );
    BOOST_CHECK_EQUAL( restored.GetPinNameOffset(), 1234 );
    auto nonzero = message;
    nonzero.mutable_definition_pin_name_offset()->set_value_nm( 700 );
    google::protobuf::Any nonzeroValue;
    nonzeroValue.PackFrom( nonzero );
    SCH_SYMBOL nonzeroRestored;
    BOOST_REQUIRE( nonzeroRestored.Deserialize( nonzeroValue ) );
    BOOST_CHECK_EQUAL( schIUScale.IUToNm( nonzeroRestored.GetLibSymbolRef()->GetPinNameOffset() ), 700 );
    BOOST_CHECK_EQUAL( nonzeroRestored.GetPinNameOffset(), 1234 );

    auto legacy = message;
    legacy.clear_library_id();
    legacy.clear_definition_pin_name_offset();
    google::protobuf::Any legacyValue;
    legacyValue.PackFrom( legacy );
    SCH_SYMBOL legacyRestored;
    BOOST_REQUIRE( legacyRestored.Deserialize( legacyValue ) );
    BOOST_CHECK_EQUAL( legacyRestored.GetLibId().Format(), definitionId.Format() );
    LIB_SYMBOL defaultLibrary( wxS( "Default" ) );
    BOOST_CHECK_EQUAL( legacyRestored.GetLibSymbolRef()->GetPinNameOffset(),
                       defaultLibrary.GetPinNameOffset() );

    // Empty alias has always meant use the placement's library link. A full
    // update must clear an existing alias, rather than silently retain it.
    message.clear_lib_name();
    encoded.PackFrom( message );
    BOOST_REQUIRE( restored.Deserialize( encoded ) );
    BOOST_CHECK( restored.UseLibIdLookup() );
    BOOST_CHECK_EQUAL( restored.GetSchSymbolLibraryName().ToStdString( wxConvUTF8 ),
                       std::string( sourceId.Format() ) );
    google::protobuf::Any before;
    restored.Serialize( before );
    auto invalid = message;
    invalid.mutable_library_id()->set_entry_name( std::string( "bad\0name", 8 ) );
    encoded.PackFrom( invalid );
    BOOST_CHECK( !restored.Deserialize( encoded ) );
    google::protobuf::Any after;
    restored.Serialize( after );
    BOOST_CHECK_EQUAL( before.SerializeAsString(), after.SerializeAsString() );
    invalid = message;
    invalid.mutable_definition_pin_name_offset()->set_value_nm( 1 );
    encoded.PackFrom( invalid );
    BOOST_CHECK( !restored.Deserialize( encoded ) );
    restored.Serialize( after );
    BOOST_CHECK_EQUAL( before.SerializeAsString(), after.SerializeAsString() );
    invalid = message;
    auto* link = invalid.mutable_library_id();
    link->GetReflection()->MutableUnknownFields( link )->AddVarint( 100, 1 );
    encoded.PackFrom( invalid );
    BOOST_CHECK( !restored.Deserialize( encoded ) );
    restored.Serialize( after );
    BOOST_CHECK_EQUAL( before.SerializeAsString(), after.SerializeAsString() );
}

BOOST_AUTO_TEST_SUITE_END()
