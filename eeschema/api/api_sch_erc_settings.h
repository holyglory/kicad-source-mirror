/* Typed live ERC settings capture. GPL-3.0-or-later. */
#ifndef API_SCH_ERC_SETTINGS_H
#define API_SCH_ERC_SETTINGS_H

#include <api/api_enums.h>
#include <erc/erc_settings.h>
#include <sch_marker.h>
#include <sch_screen.h>
#include <schematic.h>
#include <schematic/schematic_types.pb.h>
#include <map>
#include <set>
#include <stdexcept>

namespace SCH_ERC_SETTINGS
{
using MESSAGE = kiapi::schematic::types::SchematicErcSettings;

// This is a read-only projection of the same policy and live exclusions that
// native saving persists. Do not call RecordERCExclusions here: its saved cache
// can lag a user's unsaved exclusion/comment edit.
inline MESSAGE Capture( SCHEMATIC& aSchematic )
{
    MESSAGE result;
    const ERC_SETTINGS& settings = aSchematic.ErcSettings();
    for( const RC_ITEM& item : ERC_ITEM::GetItemsWithSeverities() )
    {
        const auto entry = settings.m_ERCSeverities.find( item.GetErrorCode() );
        if( item.GetSettingsKey().empty() || entry == settings.m_ERCSeverities.end() )
            continue;
        auto* rule = result.add_rule_severities();
        rule->set_rule_type( ToProtoEnum<ERCE_T, kiapi::schematic::ErcErrorType>(
                static_cast<ERCE_T>( item.GetErrorCode() ) ) );
        // Match SeverityToString's exact persisted domain.
        rule->set_severity( entry->second == RPT_SEVERITY_WARNING ? kiapi::common::types::RS_WARNING
                : entry->second == RPT_SEVERITY_IGNORE ? kiapi::common::types::RS_IGNORE
                                                      : kiapi::common::types::RS_ERROR );
    }

    for( int first = 0; first < ELECTRICAL_PINTYPES_TOTAL; ++first )
    {
        for( int second = 0; second < ELECTRICAL_PINTYPES_TOTAL; ++second )
        {
            auto* cell = result.add_pin_map();
            cell->set_first( ToProtoEnum<ELECTRICAL_PINTYPE, kiapi::common::types::ElectricalPinType>(
                    static_cast<ELECTRICAL_PINTYPE>( first ) ) );
            cell->set_second( ToProtoEnum<ELECTRICAL_PINTYPE, kiapi::common::types::ElectricalPinType>(
                    static_cast<ELECTRICAL_PINTYPE>( second ) ) );
            switch( settings.GetPinMapValue( first, second ) )
            {
            case PIN_ERROR::OK: cell->set_conflict( kiapi::schematic::types::SEPC_NONE ); break;
            case PIN_ERROR::WARNING: cell->set_conflict( kiapi::schematic::types::SEPC_WARNING ); break;
            case PIN_ERROR::PP_ERROR: cell->set_conflict( kiapi::schematic::types::SEPC_ERROR ); break;
            case PIN_ERROR::UNCONNECTED: cell->set_conflict( kiapi::schematic::types::SEPC_UNCONNECTED ); break;
            default: throw std::runtime_error( "ERC pin policy contains an unsupported native value" );
            }
        }
    }

    std::set<SCH_SCREEN*> seen;
    std::map<std::string, kiapi::schematic::ErcExclusion> exclusions;
    for( const SCH_SHEET_PATH& path : aSchematic.Hierarchy() )
    {
        SCH_SCREEN* screen = path.LastScreen();
        if( !screen || !seen.insert( screen ).second )
            continue;
        for( SCH_ITEM* item : screen->Items().OfType( SCH_MARKER_T ) )
        {
            const auto* marker = static_cast<SCH_MARKER*>( item );
            if( marker->IsExcluded() )
            {
                const ERC_EXCLUSION exclusion = ERC_EXCLUSION::FromMarker( *marker );
                exclusions.emplace( exclusion.GetSortKey(), exclusion.ToProto() );
            }
        }
    }
    for( const auto& [key, exclusion] : exclusions )
        result.add_exclusions()->CopyFrom( exclusion );
    return result;
}
}

#endif
