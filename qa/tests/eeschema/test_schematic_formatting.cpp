/* Copyright The KiCad Developers. SPDX-License-Identifier: GPL-3.0-or-later */
#include <boost/test/unit_test.hpp>
#include <api/api_sch_formatting.h>
#include <api/api_sch_field_text_modes.h>
#include <google/protobuf/unknown_field_set.h>
#include <functional>
#include <vector>

BOOST_AUTO_TEST_SUITE( SchematicFormatting )

BOOST_AUTO_TEST_CASE( FieldTextModesAreValidatedThroughOwnersAndPackedChildren )
{
    using namespace kiapi::schematic::types;
    GlobalLabel label;
    label.mutable_text()->mutable_attributes()->set_multiline( false );
    label.mutable_intersheet_refs_field()->mutable_text()->mutable_attributes()->set_multiline( true );
    BOOST_CHECK( SchematicFieldTextModesArePersistable( label ) );
    label.mutable_intersheet_refs_field()->mutable_text()->mutable_attributes()->set_multiline( false );
    const auto before = label.SerializeAsString();
    BOOST_CHECK( !SchematicFieldTextModesArePersistable( label ) );
    BOOST_CHECK_EQUAL( before, label.SerializeAsString() );

    SchematicCachedSymbol cache;
    auto* child = cache.mutable_definition()->add_items();
    child->mutable_item()->PackFrom( label.intersheet_refs_field() );
    BOOST_CHECK( !SchematicFieldTextModesArePersistable( cache ) );
    auto field = label.intersheet_refs_field();
    field.mutable_text()->mutable_attributes()->set_multiline( true );
    child->mutable_item()->PackFrom( field );
    google::protobuf::Any packed;
    packed.PackFrom( cache );
    BOOST_CHECK( SchematicFieldTextModesArePersistable( packed ) );

    SchematicSymbolInstance symbol;
    symbol.mutable_reference_field()->CopyFrom( field );
    symbol.mutable_definition()->mutable_value_field()->CopyFrom( field );
    BOOST_CHECK( SchematicFieldTextModesArePersistable( symbol ) );
    symbol.mutable_definition()->mutable_value_field()->mutable_text()->mutable_attributes()->set_multiline( false );
    BOOST_CHECK( !SchematicFieldTextModesArePersistable( symbol ) );
    SheetSymbol sheet;
    sheet.mutable_filename_field()->CopyFrom( field );
    BOOST_CHECK( SchematicFieldTextModesArePersistable( sheet ) );
    sheet.mutable_filename_field()->mutable_text()->mutable_attributes()->set_multiline( false );
    BOOST_CHECK( !SchematicFieldTextModesArePersistable( sheet ) );
}

BOOST_AUTO_TEST_CASE( TypedFormattingRetainsEveryFieldWithoutChangingUnrelatedSettings )
{
    SCHEMATIC_SETTINGS settings( nullptr, "" );
    settings.m_IntersheetRefsPrefix = wxS( "[" );
    settings.m_IntersheetRefsSuffix = wxS( "]" );
    auto original = SCH_FORMATTING::Capture( settings );
    const auto ratios = settings.DrawingRatios();
    settings.m_VariantDescriptions.emplace( wxS( "production" ), wxS( "Keep me" ) );
    auto desired = original;
    desired.set_default_line_width_nm( 254000 );
    desired.set_default_text_size_nm( 1524000 );
    desired.set_pin_symbol_size_nm( 0 );
    desired.set_connection_grid_nm( 635000 );
    desired.set_junction_size_choice( 0 );
    desired.set_hop_over_size_choice( 5 );
    desired.set_show_dnp_markers( false );
    desired.set_show_intersheet_references( true );
    desired.set_list_own_page( false );
    desired.set_short_reference_format( true );
    desired.set_reference_prefix( "Page <" );
    desired.set_reference_suffix( ">" );
    desired.mutable_operating_point()->set_voltage_precision( 10 );
    desired.mutable_operating_point()->set_current_precision( 1 );
    desired.mutable_operating_point()->set_voltage_range( "mV" );
    desired.mutable_operating_point()->set_current_range( "uA" );
    desired.mutable_unit_reference()->set_separator_ascii( '.' );
    desired.mutable_unit_reference()->set_first_id_ascii( '1' );
    std::string failure;
    BOOST_REQUIRE_MESSAGE( SCH_FORMATTING::Validate( desired, failure ), failure );
    SCH_FORMATTING::Restore( settings, desired );
    BOOST_CHECK_EQUAL( SCH_FORMATTING::Capture( settings ).SerializeAsString(), desired.SerializeAsString() );
    BOOST_CHECK( settings.DrawingRatios() == ratios );
    BOOST_CHECK( settings.m_VariantDescriptions.at( wxS( "production" ) ) == wxS( "Keep me" ) );
    SCH_FORMATTING::Restore( settings, original );
    BOOST_CHECK_EQUAL( SCH_FORMATTING::Capture( settings ).SerializeAsString(), original.SerializeAsString() );
}

BOOST_AUTO_TEST_CASE( RejectUnsupportedAndInexactFormattingBeforeAnyAssignment )
{
    SCHEMATIC_SETTINGS settings( nullptr, "" );
    auto original = SCH_FORMATTING::Capture( settings );
    std::string failure;
    using VALUE = SCH_FORMATTING::MESSAGE;
    for( const auto& corrupt : std::vector<std::function<void( VALUE& )>>{
            []( VALUE& v ) { v.set_default_line_width_nm( 126900 ); },
            []( VALUE& v ) { v.set_default_text_size_nm( 25400100 ); },
            []( VALUE& v ) { v.set_pin_symbol_size_nm( -100 ); },
            []( VALUE& v ) { v.set_connection_grid_nm( 634900 ); },
            []( VALUE& v ) { v.set_connection_grid_nm( 254000100 ); },
            []( VALUE& v ) { v.set_default_text_size_nm( v.default_text_size_nm() + 1 ); },
            []( VALUE& v ) { v.set_junction_size_choice( 6 ); },
            []( VALUE& v ) { v.set_hop_over_size_choice( 6 ); },
            []( VALUE& v ) { v.set_reference_prefix( std::string( "\0", 1 ) ); },
            []( VALUE& v ) { v.set_reference_suffix( std::string( "\xff", 1 ) ); },
            []( VALUE& v ) { v.clear_operating_point(); },
            []( VALUE& v ) { v.mutable_operating_point()->set_voltage_precision( 0 ); },
            []( VALUE& v ) { v.mutable_operating_point()->set_current_precision( 11 ); },
            []( VALUE& v ) { v.mutable_operating_point()->set_voltage_range( "mA" ); },
            []( VALUE& v ) { v.mutable_operating_point()->set_current_range( "kA" ); },
            []( VALUE& v ) { v.mutable_operating_point()->set_current_range( " A" ); },
            []( VALUE& v ) { v.mutable_operating_point()->set_voltage_range( std::string( "V\0", 2 ) ); },
            []( VALUE& v ) { v.clear_unit_reference(); },
            []( VALUE& v ) { v.mutable_unit_reference()->set_separator_ascii( 127 ); },
            []( VALUE& v ) { v.mutable_unit_reference()->set_first_id_ascii( 48 ); },
            []( VALUE& v ) { v.mutable_unit_reference()->set_first_id_ascii( 123 ); },
            []( VALUE& v ) { v.GetReflection()->MutableUnknownFields( &v )->AddVarint( 100, 1 ); } } )
    {
        auto invalid = original;
        corrupt( invalid );
        BOOST_CHECK( !SCH_FORMATTING::Validate( invalid, failure ) );
        BOOST_CHECK( !failure.empty() );
        BOOST_CHECK_EQUAL( SCH_FORMATTING::Capture( settings ).SerializeAsString(), original.SerializeAsString() );
    }
}

BOOST_AUTO_TEST_CASE( EveryNativeOperatingPointRangeRetainsItsExactDisplayMeaning )
{
    SCHEMATIC_SETTINGS settings( nullptr, "" );
    const auto original = SCH_FORMATTING::Capture( settings );
    for( const auto& prefix : { "~", "f", "p", "n", "u", "m", "", "K", "M", "G", "T", "P" } )
    {
        auto value = original;
        value.mutable_operating_point()->set_voltage_range( std::string( prefix ) + "V" );
        value.mutable_operating_point()->set_current_range( std::string( prefix ) + "A" );
        std::string failure;
        BOOST_REQUIRE_MESSAGE( SCH_FORMATTING::Validate( value, failure ), failure );
        SCH_FORMATTING::Restore( settings, value );
        BOOST_CHECK_EQUAL( SCH_FORMATTING::Capture( settings ).SerializeAsString(), value.SerializeAsString() );
    }
}

BOOST_AUTO_TEST_CASE( UnitReferenceFormattingPreservesTheNativeDisplayedSuffix )
{
    SCHEMATIC_SETTINGS settings( nullptr, "" );
    const auto original = SCH_FORMATTING::Capture( settings );
    BOOST_CHECK_EQUAL( original.unit_reference().separator_ascii(), 0u );
    BOOST_CHECK_EQUAL( original.unit_reference().first_id_ascii(), uint32_t( 'A' ) );
    std::string initialFailure;
    BOOST_REQUIRE_MESSAGE( SCH_FORMATTING::Validate( original, initialFailure ), initialFailure );
    for( int separator : { 0, int( '.' ), int( '-' ), int( '_' ) } )
    {
        for( int first : { int( 'A' ), int( 'a' ), int( '1' ) } )
        {
            auto desired = original;
            desired.mutable_unit_reference()->set_separator_ascii( separator );
            desired.mutable_unit_reference()->set_first_id_ascii( first );
            std::string failure;
            BOOST_REQUIRE_MESSAGE( SCH_FORMATTING::Validate( desired, failure ), failure );
            SCH_FORMATTING::Restore( settings, desired );
            const wxString prefix = separator ? wxString( wxChar( separator ) ) : wxString();
            const wxString one = first == '1' ? wxString( wxS( "1" ) )
                                             : wxString( wxChar( first ) );
            const wxString many = first == '1' ? wxS( "27" )
                    : first == 'A' ? wxS( "AA" ) : wxS( "aa" );
            BOOST_CHECK( settings.SubReference( 1 ) == prefix + one );
            BOOST_CHECK( settings.SubReference( 27 ) == prefix + many );
            BOOST_CHECK( settings.SubReference( 27, false ) == many );
            BOOST_CHECK( settings.SubReference( 0 ).empty() );
            BOOST_CHECK( SCH_FORMATTING::Capture( settings ).SerializeAsString() == desired.SerializeAsString() );
        }
    }
    SCH_FORMATTING::Restore( settings, original );
    BOOST_CHECK( SCH_FORMATTING::Capture( settings ).SerializeAsString() == original.SerializeAsString() );
}

BOOST_AUTO_TEST_SUITE_END()
