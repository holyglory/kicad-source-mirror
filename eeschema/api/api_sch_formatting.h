/* Persisted schematic formatting codec. GPL-3.0-or-later. */
#ifndef API_SCH_FORMATTING_H
#define API_SCH_FORMATTING_H

#include <base_units.h>
#include <schematic_settings.h>
#include <schematic/schematic_types.pb.h>
#include <string>

namespace SCH_FORMATTING
{
using MESSAGE = kiapi::schematic::types::SchematicFormattingSettings;

inline MESSAGE Capture( const SCHEMATIC_SETTINGS& aSettings )
{
    MESSAGE result;
    result.set_default_line_width_nm( schIUScale.IUToNm( aSettings.m_DefaultLineWidth ) );
    result.set_default_text_size_nm( schIUScale.IUToNm( aSettings.m_DefaultTextSize ) );
    result.set_pin_symbol_size_nm( schIUScale.IUToNm( aSettings.m_PinSymbolSize ) );
    result.set_connection_grid_nm( schIUScale.IUToNm( aSettings.m_ConnectionGridSize ) );
    result.set_junction_size_choice( aSettings.m_JunctionSizeChoice );
    result.set_hop_over_size_choice( aSettings.m_HopOverSizeChoice );
    result.set_show_dnp_markers( aSettings.m_ShowDNPMarkers );
    result.set_show_intersheet_references( aSettings.m_IntersheetRefsShow );
    result.set_list_own_page( aSettings.m_IntersheetRefsListOwnPage );
    result.set_short_reference_format( aSettings.m_IntersheetRefsFormatShort );
    result.set_reference_prefix( aSettings.m_IntersheetRefsPrefix.ToUTF8().data() );
    result.set_reference_suffix( aSettings.m_IntersheetRefsSuffix.ToUTF8().data() );
    auto* operating = result.mutable_operating_point();
    operating->set_voltage_precision( aSettings.m_OPO_VPrecision );
    operating->set_voltage_range( aSettings.m_OPO_VRange.ToUTF8().data() );
    operating->set_current_precision( aSettings.m_OPO_IPrecision );
    operating->set_current_range( aSettings.m_OPO_IRange.ToUTF8().data() );
    result.mutable_unit_reference()->set_separator_ascii( aSettings.m_SubpartIdSeparator );
    result.mutable_unit_reference()->set_first_id_ascii( aSettings.m_SubpartFirstId );
    return result;
}

// Validate the whole candidate before assigning any live setting. Do not
// round an unrepresentable XML value or silently select application defaults.
inline bool Validate( const MESSAGE& aValue, std::string& aFailure )
{
    auto known = aValue;
    known.DiscardUnknownFields();
    if( known.ByteSizeLong() != aValue.ByteSizeLong() )
    {
        aFailure = "Formatting contains unsupported fields";
        return false;
    }
    const auto distance = []( int64_t nm, int minimumMils, int maximumMils )
    {
        return nm >= int64_t( minimumMils ) * 25400
                && nm <= int64_t( maximumMils ) * 25400
                && nm % schIUScale.IUToNm( 1 ) == 0;
    };
    if( !distance( aValue.default_line_width_nm(), 5, 1000 )
            || !distance( aValue.default_text_size_nm(), 5, 1000 )
            || !distance( aValue.pin_symbol_size_nm(), 0, 1000 )
            || !distance( aValue.connection_grid_nm(), MIN_CONNECTION_GRID_MILS, 10000 ) )
    {
        aFailure = "Formatting distances must fit native project ranges and the 100 nm unit grid";
        return false;
    }
    if( aValue.junction_size_choice() > 5 || aValue.hop_over_size_choice() > 5 )
    {
        aFailure = "Junction and hop-over sizes must identify a native choice from 0 to 5";
        return false;
    }
    const auto range = []( const std::string& value, char unit )
    {
        if( value == std::string( 1, unit ) ) return true;
        return value.size() == 2 && value[1] == unit
                && std::string( "~fpnumKMGTP" ).find( value[0] ) != std::string::npos;
    };
    const auto& operating = aValue.operating_point();
    if( !aValue.has_unit_reference() || aValue.unit_reference().separator_ascii() > 126
            || aValue.unit_reference().first_id_ascii() < '1'
            || aValue.unit_reference().first_id_ascii() > 'z' )
    {
        aFailure = "Unit-reference formatting must use the native persisted character ranges";
        return false;
    }
    if( !aValue.has_operating_point() || operating.voltage_precision() < 1
            || operating.voltage_precision() > 10 || operating.current_precision() < 1
            || operating.current_precision() > 10 || !range( operating.voltage_range(), 'V' )
            || !range( operating.current_range(), 'A' ) )
    {
        aFailure = "Operating-point formatting requires precision 1 to 10 and native voltage/current display ranges";
        return false;
    }
    for( const auto* text : { &aValue.reference_prefix(), &aValue.reference_suffix() } )
    {
        if( text->find( '\0' ) != std::string::npos
                || wxString::FromUTF8( *text ).ToStdString( wxConvUTF8 ) != *text )
        {
            aFailure = "Inter-sheet reference delimiters must be valid UTF-8 without NUL characters";
            return false;
        }
    }
    return true;
}

// Only a validated candidate or a previously captured native undo value may
// reach here. View/cache refresh and commit ownership belong to the caller.
inline void Restore( SCHEMATIC_SETTINGS& aSettings, const MESSAGE& aValue )
{
    aSettings.m_DefaultLineWidth = schIUScale.NmToIU( aValue.default_line_width_nm() );
    aSettings.m_DefaultTextSize = schIUScale.NmToIU( aValue.default_text_size_nm() );
    aSettings.m_PinSymbolSize = schIUScale.NmToIU( aValue.pin_symbol_size_nm() );
    aSettings.m_ConnectionGridSize = schIUScale.NmToIU( aValue.connection_grid_nm() );
    aSettings.m_JunctionSizeChoice = aValue.junction_size_choice();
    aSettings.m_HopOverSizeChoice = aValue.hop_over_size_choice();
    aSettings.m_ShowDNPMarkers = aValue.show_dnp_markers();
    aSettings.m_IntersheetRefsShow = aValue.show_intersheet_references();
    aSettings.m_IntersheetRefsListOwnPage = aValue.list_own_page();
    aSettings.m_IntersheetRefsFormatShort = aValue.short_reference_format();
    aSettings.m_IntersheetRefsPrefix = wxString::FromUTF8( aValue.reference_prefix() );
    aSettings.m_IntersheetRefsSuffix = wxString::FromUTF8( aValue.reference_suffix() );
    aSettings.m_OPO_VPrecision = aValue.operating_point().voltage_precision();
    aSettings.m_OPO_VRange = wxString::FromUTF8( aValue.operating_point().voltage_range() );
    aSettings.m_OPO_IPrecision = aValue.operating_point().current_precision();
    aSettings.m_OPO_IRange = wxString::FromUTF8( aValue.operating_point().current_range() );
    aSettings.m_SubpartIdSeparator = aValue.unit_reference().separator_ascii();
    aSettings.m_SubpartFirstId = aValue.unit_reference().first_id_ascii();
}
}
#endif
