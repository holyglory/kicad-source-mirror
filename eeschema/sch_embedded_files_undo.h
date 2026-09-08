/* Schematic embedded-file transaction state. GPL-3.0-or-later. */
#pragma once

#include <eda_item.h>
#include <embedded_files.h>

class SCH_EMBEDDED_FILES_UNDO_ITEM : public EDA_ITEM
{
public:
    explicit SCH_EMBEDDED_FILES_UNDO_ITEM( const EMBEDDED_FILES& aFiles ) :
            EDA_ITEM( SCH_EMBEDDED_FILES_UNDO_T ), m_files( aFiles, true )
    {
        SetFlags( UR_TRANSIENT );
    }

    void Swap( EMBEDDED_FILES& aFiles ) noexcept { m_files.SwapData( aFiles ); }
    wxString GetClass() const override { return wxT( "SCH_EMBEDDED_FILES_UNDO_ITEM" ); }
#if defined(DEBUG)
    void Show( int, std::ostream& ) const override {}
#endif

private:
    EMBEDDED_FILES m_files;
};
