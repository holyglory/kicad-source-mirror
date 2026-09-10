/* Copyright The KiCad Developers, see AUTHORS.txt for contributors.
 * SPDX-License-Identifier: GPL-2.0-or-later
 */
#include "caption_action.h"
#import <objc/runtime.h>
#include <stdexcept>

@interface KICAD_CAPTION_ACTION_TARGET : NSObject
{
@public
    std::function<void()> callback;
}
- (void)activate:(id)sender;
@end

@implementation KICAD_CAPTION_ACTION_TARGET
- (void)activate:(id)sender
{
    if( callback ) callback();
}
@end

std::function<void( bool, bool )> KIPLATFORM::UI::AddMacCaptionAction( NSWindow* window,
        NSString* title, std::function<void()> aAction )
{
    if( !window ) throw std::logic_error( "Caption action requires an existing native window." );
    NSTitlebarAccessoryViewController* controller = [[NSTitlebarAccessoryViewController alloc] init];
    NSButton* button = [[NSButton alloc] initWithFrame:NSMakeRect( 0, 0, 84, 28 )];
    [button setTitle:title];
    [button setAccessibilityLabel:title];
    [button setBezelStyle:NSBezelStyleRounded];
    [button setButtonType:NSButtonTypeMomentaryPushIn];
    KICAD_CAPTION_ACTION_TARGET* target = [[KICAD_CAPTION_ACTION_TARGET alloc] init];
    target->callback = std::move( aAction );
    [button setTarget:target];
    [button setAction:@selector(activate:)];
    static char targetKey;
    objc_setAssociatedObject( button, &targetKey, target, OBJC_ASSOCIATION_RETAIN_NONATOMIC );
    [target release];
    [controller setView:button];
    [controller setLayoutAttribute:NSLayoutAttributeRight];
    [controller setHidden:YES];
    [window addTitlebarAccessoryViewController:controller];
    [button release];
    [controller release];
    // NSWindow owns the controller and its view. The caller's setter has the
    // same window-bound lifetime as on GTK; no global/window-swapping state.
    return [controller, button]( bool visible, bool enabled )
    {
        [button setEnabled:enabled];
        [controller setHidden:!visible];
    };
}
