/* Copyright The KiCad Developers, see AUTHORS.txt for contributors.
 * SPDX-License-Identifier: GPL-2.0-or-later
 */
#pragma once
#import <AppKit/AppKit.h>
#include <functional>
namespace KIPLATFORM::UI
{
std::function<void( bool, bool )> AddMacCaptionAction( NSWindow* aWindow, NSString* aLabel,
        std::function<void()> aAction );
}
