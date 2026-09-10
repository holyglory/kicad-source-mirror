/* Copyright The KiCad Developers, see AUTHORS.txt for contributors.
 * SPDX-License-Identifier: GPL-2.0-or-later
 */
#pragma once
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <functional>
#include <string>

namespace KIPLATFORM::UI
{
/** A window-owned non-client action with standard accessibility and a keyboard
 * equivalent in the system menu. Calls and callbacks run on the owning thread. */
std::function<void( bool, bool )> AddWindowsCaptionAction( HWND aWindow,
        const std::wstring& aLabel, std::function<void()> aAction );
}
