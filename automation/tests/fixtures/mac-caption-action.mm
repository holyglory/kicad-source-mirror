#include "caption_action.h"
#include <stdio.h>
#include <stdexcept>

static void require( bool condition, const char* message )
{
    if( !condition ) throw std::runtime_error( message );
}

int main( int argc, char** argv )
{
    @autoreleasepool
    {
        if( argc != 2 ) return 2;
        [NSApplication sharedApplication];
        [NSApp setActivationPolicy:NSApplicationActivationPolicyRegular];
        [NSApp finishLaunching];
        try
        {
            int clicks = 0, otherClicks = 0;
            NSWindow* first = [[NSWindow alloc] initWithContentRect:NSMakeRect( 100, 100, 800, 400 )
                styleMask:NSWindowStyleMaskTitled | NSWindowStyleMaskClosable | NSWindowStyleMaskResizable
                backing:NSBackingStoreBuffered defer:NO];
            NSWindow* second = [[NSWindow alloc] initWithContentRect:NSMakeRect( 200, 200, 600, 300 )
                styleMask:NSWindowStyleMaskTitled | NSWindowStyleMaskClosable
                backing:NSBackingStoreBuffered defer:NO];
            [first setReleasedWhenClosed:NO]; [second setReleasedWhenClosed:NO];
            [first setTitle:@"KiCad update control fixture"];
            auto state = KIPLATFORM::UI::AddMacCaptionAction( first, @"Update", [&] { clicks++; } );
            auto otherState = KIPLATFORM::UI::AddMacCaptionAction( second, @"Update", [&] { otherClicks++; } );
            auto* controller = [[first titlebarAccessoryViewControllers] firstObject];
            auto* otherController = [[second titlebarAccessoryViewControllers] firstObject];
            NSButton* button = (NSButton*)[controller view];
            require( [controller isHidden], "A new caption control must be hidden" );
            require( [[button accessibilityLabel] isEqualToString:@"Update"], "Accessible action name changed" );
            [first makeKeyAndOrderFront:nil]; [second orderFront:nil];
            for( bool dark : { false, true } )
            {
                [first setAppearance:[NSAppearance appearanceNamed:dark ? NSAppearanceNameDarkAqua : NSAppearanceNameAqua]];
                for( int width : { 800, 340, 800 } )
                {
                    [first setContentSize:NSMakeSize( width, 400 )];
                    state( true, true );
                    [first displayIfNeeded];
                    [[NSRunLoop currentRunLoop] runUntilDate:[NSDate dateWithTimeIntervalSinceNow:0.02]];
                    require( ![controller isHidden] && [button isEnabled], "Caption control did not become available" );
                    require( NSWidth( [button frame] ) >= 72 && NSHeight( [button frame] ) >= 20,
                        "Caption action became too small" );
                    NSRect frame = [button convertRect:[button bounds] toView:nil];
                    require( NSMinX( frame ) >= 0 && NSMaxX( frame ) <= NSWidth( [first frame] ), "Caption action extends beyond window" );
                    const int before = clicks;
                    [button performClick:nil];
                    require( clicks == before + 1 && otherClicks == 0, "Caption action reached the wrong owner" );
                    state( true, false );
                    [button performClick:nil];
                    require( clicks == before + 1, "Disabled caption action fired" );
                    require( [otherController isHidden], "Independent window visibility changed" );
                    state( false, false );
                    require( [controller isHidden], "Caption action failed to hide" );
                    state( true, true );
                    [first displayIfNeeded];
                    if( width == 340 )
                    {
                        NSView* frameView = [[first contentView] superview];
                        NSBitmapImageRep* image = [frameView bitmapImageRepForCachingDisplayInRect:[frameView bounds]];
                        [frameView cacheDisplayInRect:[frameView bounds] toBitmapImageRep:image];
                        NSData* data = [image representationUsingType:NSBitmapImageFileTypePNG properties:@{}];
                        NSString* path = [NSString stringWithFormat:@"%s/%s.png", argv[1], dark ? "dark" : "light"];
                        require( data && [data writeToFile:path atomically:YES], "Could not retain rendered caption image" );
                    }
                }
            }
            otherState( true, true );
            [(NSButton*)[otherController view] performClick:nil];
            require( otherClicks == 1 && clicks == 6, "Independent caption callback failed" );
            [first close]; [second close];
            [first release]; [second release];
            puts( "{\"schemaVersion\":1,\"renderedControlVerified\":true,\"enabledDisabledVerified\":true,\"twoWindowsIsolated\":true,\"lightDarkNarrowWideVerified\":true,\"applicationUpdateJourneyVerified\":false}" );
            return 0;
        }
        catch( const std::exception& error ) { fprintf( stderr, "%s\n", error.what() ); return 1; }
    }
}
