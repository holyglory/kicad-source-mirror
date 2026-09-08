/*
 * Copyright The KiCad Developers, see AUTHORS.txt for contributors.
 * SPDX-License-Identifier: GPL-3.0-or-later
 */
#pragma once

#include <memory>
#include <map>
#include <unordered_map>
#include <kiid.h>
#include <wx/string.h>
#include <api/schematic/schematic_types.pb.h>

class LIB_SYMBOL;
class SCH_SYMBOL_CACHE_STATE;

// Whole-cache codecs preserve the destination on rejection. Empty input is
// explicit empty state; no placed symbol is needed to retain unused entries.
bool PackCachedSymbols(
        google::protobuf::RepeatedPtrField<kiapi::schematic::types::SchematicCachedSymbol>& aOutput,
        const std::map<wxString, LIB_SYMBOL*>& aCache );
bool UnpackCachedSymbols(
        const google::protobuf::RepeatedPtrField<kiapi::schematic::types::SchematicCachedSymbol>& aInput,
        SCH_SYMBOL_CACHE_STATE& aOutput );

// Rejects derived definitions; cache records never flatten external libraries.
bool PackCachedSymbol( kiapi::schematic::types::SchematicCachedSymbol& aOutput,
                       const wxString& aCacheKey, const LIB_SYMBOL& aLibrary );
std::unique_ptr<LIB_SYMBOL> UnpackCachedSymbol(
        const kiapi::schematic::types::SchematicCachedSymbol& aInput );

// Definition body only: placement identity and pin-display settings have
// separate owners. Placed symbols replace the pin list with instance IDs.
void PackSymbolDefinition( kiapi::schematic::types::SchematicSymbol& aOutput,
                           const LIB_SYMBOL& aLibrary, bool aIncludePins = true );

// Decode into a private owned definition. The caller's library and optional
// active-alternate map are unchanged on rejection. Placement identity,
// geometry, transforms and instance records are not owned by this helper.
std::unique_ptr<LIB_SYMBOL> UnpackSymbolDefinition(
        const kiapi::schematic::types::SchematicSymbol& aDefinition,
        std::unordered_map<KIID, wxString>* aActiveAlternates = nullptr );
