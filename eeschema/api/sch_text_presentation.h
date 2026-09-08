/*
 * Copyright The KiCad Developers, see AUTHORS.txt for contributors.
 * SPDX-License-Identifier: GPL-3.0-or-later
 */
#ifndef SCH_TEXT_PRESENTATION_H
#define SCH_TEXT_PRESENTATION_H

#include <callback_gal.h>
#include <font/font.h>
#include <geometry/shape_segment.h>
#include <geometry/shape_line_chain.h>
#include <sch_painter.h>
#include <sch_text.h>
#include <optional>

// Persisted ordinary text only. Device text, labels and hover/selection
// adornments have different painter placement rules.
inline std::optional<BOX2I> SchTextPresentationBounds(
        const SCH_TEXT& aText, const SCH_RENDER_SETTINGS& aSettings )
{
    KIFONT::FONT* font = aText.GetDrawFont( &aSettings );
    if( aText.Type() != SCH_TEXT_T || aText.GetLayer() == LAYER_DEVICE
            || !font || ( !font->IsStroke() && !font->IsOutline() ) )
        return std::nullopt;

    const int pen = aText.GetEffectiveTextPenWidth( aSettings.GetDefaultPenWidth() );
    TEXT_ATTRIBUTES attrs = aText.GetAttributes();
    attrs.m_Angle = aText.GetDrawRotation();
    attrs.m_StrokeWidth = pen;
    std::optional<BOX2I> bounds;
    KIGFX::GAL_DISPLAY_OPTIONS options;
    CALLBACK_GAL gal( options,
            [&]( const VECTOR2I& start, const VECTOR2I& end )
            {
                BOX2I stroke = SHAPE_SEGMENT( start, end, pen ).BBox();
                if( bounds ) bounds->Merge( stroke );
                else bounds = stroke;
            },
            [&]( const SHAPE_LINE_CHAIN& outline )
            {
                if( bounds ) bounds->Merge( outline.BBox() );
                else bounds = outline.BBox();
            } );
    const wxString shownText = aText.GetShownText( true );
    font->Draw( &gal, shownText,
                aText.GetDrawPos() + aText.GetSchematicTextOffset( &aSettings )
                    + aText.GetOffsetToMatchSCH_FIELD( nullptr, shownText ),
                attrs, aText.GetFontMetrics() );
    return bounds;
}

#endif
