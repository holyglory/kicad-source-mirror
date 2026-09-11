#include "caption_action.h"
#include <stdio.h>
#include <stdexcept>
#include <unistd.h>
#include <string.h>

static void require( bool condition, const char* message )
{
    if( !condition ) throw std::runtime_error( message );
}

@interface KICAD_MENU_DRIVER_FIXTURE : NSObject
@property(copy) NSString* directory;
- (void) selectPCB:(id)sender;
@end

@implementation KICAD_MENU_DRIVER_FIXTURE
- (void) selectPCB:(id)sender
{
    [@"selected" writeToFile:[self.directory stringByAppendingPathComponent:@"menu-selected"] atomically:YES
        encoding:NSUTF8StringEncoding error:nil];
}
@end

int main( int argc, char** argv )
{
    @autoreleasepool
    {
        bool external = argc == 3 && strcmp( argv[1], "--external-ui" ) == 0;
        if( argc != 2 && !external ) return 2;
        [NSApplication sharedApplication];
        [NSApp setActivationPolicy:NSApplicationActivationPolicyRegular];
        [NSApp finishLaunching];
        if( external )
        {
            NSString* directory = [NSString stringWithUTF8String:argv[2]];
            KICAD_MENU_DRIVER_FIXTURE* menuTarget = [[KICAD_MENU_DRIVER_FIXTURE alloc] init];
            menuTarget.directory = directory;
            NSMenu* bar = [[[NSMenu alloc] init] autorelease];
            NSMenuItem* appItem = [[[NSMenuItem alloc] initWithTitle:@"Fixture" action:nil keyEquivalent:@""] autorelease];
            [appItem setSubmenu:[[[NSMenu alloc] initWithTitle:@"Fixture"] autorelease]]; [bar addItem:appItem];
            NSMenuItem* tools = [[[NSMenuItem alloc] initWithTitle:@"Tools" action:nil keyEquivalent:@""] autorelease];
            NSMenu* toolsMenu = [[[NSMenu alloc] initWithTitle:@"Tools"] autorelease];
            [toolsMenu setAutoenablesItems:NO];
            for( NSString* title in @[ @"PCB Editor", @"Disabled Editor" ] )
            {
                NSMenuItem* item = [[[NSMenuItem alloc] initWithTitle:title action:@selector(selectPCB:) keyEquivalent:@""] autorelease];
                [item setTarget:menuTarget]; [item setEnabled:[title isEqualToString:@"PCB Editor"]];
                [toolsMenu addItem:item];
            }
            [tools setSubmenu:toolsMenu]; [bar addItem:tools]; [NSApp setMainMenu:bar];
            NSWindow* window = [[NSWindow alloc] initWithContentRect:NSMakeRect( 100, 100, 600, 350 )
                styleMask:NSWindowStyleMaskTitled | NSWindowStyleMaskClosable | NSWindowStyleMaskResizable
                backing:NSBackingStoreBuffered defer:NO];
            [window setReleasedWhenClosed:NO];
            [window setTitle:@"KiCad external UI driver fixture"];
            auto state = KIPLATFORM::UI::AddMacCaptionAction( window, @"Update", [directory]
            {
                [@"{\"schemaVersion\":1,\"pressed\":true,\"applicationUpdateJourneyVerified\":false}"
                    writeToFile:[directory stringByAppendingPathComponent:@"pressed.json"] atomically:YES
                    encoding:NSUTF8StringEncoding error:nil];
            } );
            state( true, true );
            // Explicit hidden-but-enabled negative control for the AX verifier.
            // Do not infer rendered visibility from controller state alone.
            auto hide = KIPLATFORM::UI::AddMacCaptionAction( window, @"Hide", [window]
            {
                auto* controller = [[window titlebarAccessoryViewControllers] firstObject];
                NSButton* button = (NSButton*)[controller view];
                [button setHidden:YES]; [button setEnabled:YES]; [controller setHidden:YES];
            } );
            auto show = KIPLATFORM::UI::AddMacCaptionAction( window, @"Show", [state] { state( true, true ); } );
            hide( true, true ); show( true, true );
            [window makeKeyAndOrderFront:nil];
            [window displayIfNeeded];
            NSWindow* cover = [[NSWindow alloc] initWithContentRect:NSMakeRect( 100, 100, 600, 350 )
                styleMask:NSWindowStyleMaskTitled | NSWindowStyleMaskClosable
                backing:NSBackingStoreBuffered defer:NO];
            [cover setReleasedWhenClosed:NO]; [cover setTitle:@"KiCad occluding fixture"];
            [cover setFrame:[window frame] display:YES]; [cover makeKeyAndOrderFront:nil]; [cover displayIfNeeded];
            [@"ready" writeToFile:[directory stringByAppendingPathComponent:@"ready"] atomically:YES
                encoding:NSUTF8StringEncoding error:nil];
            [NSApp run];
            [menuTarget release];
            [cover release];
            [window release];
            return 0;
        }
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
            require( [button isHidden] && ![button isEnabled], "A hidden initial control must not be actionable" );
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
                    state( false, true );
                    require( [controller isHidden] && [button isHidden] && ![button isEnabled], "Hidden caption action is still actionable" );
                    [button performClick:nil];
                    require( clicks == before + 1, "Hidden caption action fired" );
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
