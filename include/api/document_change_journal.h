/* KiCad automation change journal. GPL-3.0-or-later. */
#ifndef KICAD_DOCUMENT_CHANGE_JOURNAL_H
#define KICAD_DOCUMENT_CHANGE_JOURNAL_H

#include <cstdint>
#include <deque>
#include <limits>
#include <stdexcept>
#include <string>
#include <vector>

// Owned by the document, used only on its editor thread. This is bounded
// recovery history, not a transport or a second undo stack.
class DOCUMENT_CHANGE_JOURNAL
{
public:
    enum class KIND { COMMIT, UNDO, REDO };
    struct ENTRY
    {
        uint64_t sequence;
        KIND kind;
        std::string description;
        std::string originId;
        std::string operationId;
    };
    struct READ_RESULT
    {
        bool resetRequired;
        std::vector<ENTRY> entries;
    };

    explicit DOCUMENT_CHANGE_JOURNAL( size_t aCapacity = 256 ) : m_capacity( aCapacity )
    {
        if( !aCapacity )
            throw std::invalid_argument( "Journal capacity must be positive" );
    }

    void Reset( const std::string& aEpoch )
    {
        if( aEpoch.empty() || aEpoch == m_epoch )
            throw std::invalid_argument( "A reset requires a new document epoch" );

        m_epoch = aEpoch;
        m_sequence = 0;
        m_entries.clear();
    }

    void Append( KIND aKind, const std::string& aDescription,
                 const std::string& aOriginId = {}, const std::string& aOperationId = {} )
    {
        if( m_epoch.empty() || m_sequence == std::numeric_limits<uint64_t>::max() )
            throw std::overflow_error( "Journal requires a new document epoch" );

        // Allocate before publishing the new sequence.
        m_entries.push_back( { m_sequence + 1, aKind, aDescription, aOriginId, aOperationId } );
        ++m_sequence;

        if( m_entries.size() > m_capacity )
            m_entries.pop_front();
    }

    READ_RESULT ReadAfter( const std::string& aEpoch, uint64_t aSequence ) const
    {
        if( aEpoch != m_epoch || aSequence > m_sequence
                || ( !m_entries.empty() && aSequence < m_entries.front().sequence - 1 ) )
            return { true, {} };

        READ_RESULT result{ false, {} };

        for( const ENTRY& entry : m_entries )
        {
            if( entry.sequence > aSequence )
                result.entries.push_back( entry );
        }

        return result;
    }

    const std::string& Epoch() const { return m_epoch; }
    uint64_t Sequence() const { return m_sequence; }

private:
    size_t m_capacity;
    std::string m_epoch;
    uint64_t m_sequence = 0;
    std::deque<ENTRY> m_entries;
};

#endif
