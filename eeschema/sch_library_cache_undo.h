/* Native schematic cache transaction ownership. GPL-3.0-or-later. */
#pragma once

#include <eda_item.h>
#include <sch_screen.h>
#include <sch_symbol_cache_state.h>

class SCH_SYMBOL_CACHE_EDIT_SCOPE
{
public:
    explicit SCH_SYMBOL_CACHE_EDIT_SCOPE( SCH_SCREEN& aScreen ) : m_screen( aScreen )
    {
        m_screen.BeginManagedSymbolCache();
    }
    ~SCH_SYMBOL_CACHE_EDIT_SCOPE() { m_screen.EndManagedSymbolCache(); }
    SCH_SYMBOL_CACHE_EDIT_SCOPE( const SCH_SYMBOL_CACHE_EDIT_SCOPE& ) = delete;
    SCH_SYMBOL_CACHE_EDIT_SCOPE& operator=( const SCH_SYMBOL_CACHE_EDIT_SCOPE& ) = delete;
private:
    SCH_SCREEN& m_screen;
};

class SCH_LIBRARY_CACHE_UNDO_ITEM : public EDA_ITEM
{
public:
    explicit SCH_LIBRARY_CACHE_UNDO_ITEM( SCH_SCREEN& aScreen ) :
            EDA_ITEM( SCH_LIBRARY_CACHE_UNDO_T ), m_screen( &aScreen ),
            m_saved( aScreen.GetLibSymbols() ), m_wasDirty( aScreen.IsContentModified() )
    {
        m_screen->IncRefCount();
        SetFlags( UR_TRANSIENT );
    }
    ~SCH_LIBRARY_CACHE_UNDO_ITEM() override
    {
        m_screen->DecRefCount();
        if( m_screen->GetRefCount() == 0 )
            delete m_screen;
    }
    SCH_SCREEN& Screen() const { return *m_screen; }
    SCH_SYMBOL_CACHE_STATE CaptureCurrent() const
    {
        return SCH_SYMBOL_CACHE_STATE( m_screen->GetLibSymbols() );
    }
    // Called after graphical undo/redo, using the state captured BEFORE it.
    // Cache changes caused by graphical restoration are never saved as redo.
    void RestoreForUndo( SCH_SYMBOL_CACHE_STATE aBefore )
    {
        m_screen->SwapLibSymbolCache( m_saved );
        m_saved = std::move( aBefore );
        m_screen->SetContentModified();
    }
    void RestoreForRollback()
    {
        m_screen->SwapLibSymbolCache( m_saved );
        m_screen->SetContentModified( m_wasDirty );
    }
    wxString GetClass() const override { return wxT( "SCH_LIBRARY_CACHE_UNDO_ITEM" ); }
#if defined(DEBUG)
    void Show( int, std::ostream& ) const override {}
#endif
private:
    SCH_SCREEN* m_screen;
    SCH_SYMBOL_CACHE_STATE m_saved;
    bool m_wasDirty;
};
