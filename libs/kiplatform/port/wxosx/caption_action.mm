/* Copyright The KiCad Developers, see AUTHORS.txt for contributors.
 * SPDX-License-Identifier: GPL-2.0-or-later
 */
#include "caption_action.h"
#import <objc/runtime.h>
#include <stdexcept>

namespace
{
const void* callbackKey()
{
    // Selectors are interned process-wide; a static address would differ between
    // the copies of this static library linked into manager/editor images.
    return reinterpret_cast<const void*>( sel_registerName( "kicadCaptionCallbackV1" ) );
}

void activate( id target, SEL, id sender )
{
    if( ![sender isKindOfClass:[NSButton class]] || ![(NSButton*)sender isEnabled]
        || [(NSButton*)sender isHiddenOrHasHiddenAncestor] ) return;
    auto callback = (void (^)( void )) objc_getAssociatedObject( target, callbackKey() );
    if( callback ) callback();
}

Class callbackClass()
{
    // UI creation is serialized on the AppKit thread. Register once at runtime,
    // not as duplicate Objective-C class metadata in every linked native image.
    if( ![NSThread isMainThread] ) throw std::logic_error( "Caption actions require the AppKit thread." );
    Class result = objc_lookUpClass( "KICAD_CAPTION_CALLBACK_V1" );
    if( !result )
    {
        result = objc_allocateClassPair( [NSObject class], "KICAD_CAPTION_CALLBACK_V1", 0 );
        if( !result || !class_addMethod( result, @selector(activate:), reinterpret_cast<IMP>( activate ), "v@:@" ) )
            throw std::logic_error( "Caption callback class could not be registered." );
        objc_registerClassPair( result );
    }
    return result;
}
}

std::function<void( bool, bool )> KIPLATFORM::UI::AddMacCaptionAction( NSWindow* window,
        NSString* title, std::function<void()> aAction )
{
    if( !window ) throw std::logic_error( "Caption action requires an existing native window." );
    NSTitlebarAccessoryViewController* controller = [[NSTitlebarAccessoryViewController alloc] init];
    NSButton* button = [[NSButton alloc] initWithFrame:NSMakeRect( 0, 0, 84, 28 )];
    [button setHidden:YES];
    [button setEnabled:NO];
    [button setTitle:title];
    [button setAccessibilityLabel:title];
    [button setBezelStyle:NSBezelStyleRounded];
    [button setButtonType:NSButtonTypeMomentaryPushIn];
    id target = [[callbackClass() alloc] init];
    auto callback = std::move( aAction );
    objc_setAssociatedObject( target, callbackKey(), ^{ callback(); }, OBJC_ASSOCIATION_COPY_NONATOMIC );
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
        [button setEnabled:visible && enabled];
        [button setHidden:!visible];
        [controller setHidden:!visible];
    };
}
