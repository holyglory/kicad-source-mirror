/* Owned schematic cache transaction state. GPL-3.0-or-later. */
#pragma once

#include <lib_symbol.h>
#include <map>
#include <memory>
#include <stdexcept>

// A cache owns its definitions independently of placed instances. This value
// retains that ownership across a transaction, including unused definitions.
class SCH_SYMBOL_CACHE_STATE
{
public:
    SCH_SYMBOL_CACHE_STATE() = default;

    explicit SCH_SYMBOL_CACHE_STATE( const std::map<wxString, LIB_SYMBOL*>& aNative )
    {
        SCH_SYMBOL_CACHE_STATE candidate;
        for( const auto& [key, symbol] : aNative )
        {
            if( !symbol )
                throw std::invalid_argument( "Cannot capture a null schematic cache definition" );
            if( !candidate.Insert( key, std::make_unique<LIB_SYMBOL>( *symbol ) ) )
                throw std::invalid_argument( "Cannot capture an invalid schematic cache key" );
        }
        m_symbols.swap( candidate.m_symbols );
    }

    ~SCH_SYMBOL_CACHE_STATE() { Clear(); }
    SCH_SYMBOL_CACHE_STATE( SCH_SYMBOL_CACHE_STATE&& aOther ) noexcept
    {
        m_symbols.swap( aOther.m_symbols );
    }
    SCH_SYMBOL_CACHE_STATE& operator=( SCH_SYMBOL_CACHE_STATE&& aOther ) noexcept
    {
        if( this != &aOther )
        {
            Clear();
            m_symbols.swap( aOther.m_symbols );
        }
        return *this;
    }

    bool Insert( const wxString& aKey, std::unique_ptr<LIB_SYMBOL> aSymbol )
    {
        if( aKey.IsEmpty() || !aSymbol )
            return false;
        auto [entry, inserted] = m_symbols.try_emplace( aKey, nullptr );
        if( inserted )
            entry->second = aSymbol.release();
        return inserted;
    }

    const auto& Symbols() const { return m_symbols; }

    // After validation, ownership exchange allocates nothing, including on
    // rollback. This value receives the former native cache; aNative owns ours.
    // Notifications, consistency with placed objects, and revision admission
    // belong to the surrounding native commit, not this ownership primitive.
    void Swap( std::map<wxString, LIB_SYMBOL*>& aNative )
    {
        for( const auto& [key, symbol] : aNative )
        {
            if( !symbol )
                throw std::invalid_argument( "Cannot exchange a null schematic cache definition" );
        }
        m_symbols.swap( aNative );
    }

private:
    void Clear() noexcept
    {
        for( const auto& [key, symbol] : m_symbols )
            delete symbol;
        m_symbols.clear();
    }

    std::map<wxString, LIB_SYMBOL*> m_symbols;
};
