/* Root-page records belong to schematic files, not individual placements.
 * GPL-3.0-or-later. */
#pragma once

#include <schematic.h>
#include <sch_sheet.h>
#include <optional>

struct SCH_ROOT_INSTANCE_STATE
{
    std::optional<wxString> pageNumber;
    bool conflict = false;
};

// Native loading can leave a root record on only the first placement of a
// shared screen. Missing copies inherit that file's record; contradictory
// nonempty copies must be resolved explicitly, never by traversal order.
inline SCH_ROOT_INSTANCE_STATE ResolveRootInstance( const SCHEMATIC* aSchematic, const SCH_SHEET& aSheet )
{
    SCH_ROOT_INSTANCE_STATE result;
    auto include = [&]( const SCH_SHEET* sheet )
    {
        if( !sheet || !sheet->HasRootInstance() )
            return;
        const wxString& page = sheet->GetRootInstance().m_PageNumber;
        if( result.pageNumber && *result.pageNumber != page )
            result.conflict = true;
        else
            result.pageNumber = page;
    };
    include( &aSheet );
    if( aSchematic && aSheet.GetScreen() )
        for( const SCH_SHEET_PATH& path : aSchematic->Hierarchy() )
            if( path.LastScreen() == aSheet.GetScreen() )
                include( path.Last() );
    return result;
}

inline bool HasRootInstanceConflicts( const SCHEMATIC& aSchematic )
{
    for( const SCH_SHEET_PATH& path : aSchematic.Hierarchy() )
        if( ResolveRootInstance( &aSchematic, *path.Last() ).conflict )
            return true;
    return false;
}
