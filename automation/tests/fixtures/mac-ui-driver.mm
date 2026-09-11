// Compiled, same-host QA driver. No permission prompts or system policy changes.
#import <AppKit/AppKit.h>
#import <ApplicationServices/ApplicationServices.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <vector>
#include <limits.h>
#include <libproc.h>
#include <sys/proc_info.h>
#include <sys/sysctl.h>

static void emit( NSDictionary* value )
{
    NSData* data = [NSJSONSerialization dataWithJSONObject:value options:0 error:nil];
    if( data ) { fwrite( [data bytes], 1, [data length], stdout ); puts( "" ); }
}

static NSString* textAttribute( AXUIElementRef element, CFStringRef attribute )
{
    CFTypeRef value = nullptr;
    if( AXUIElementCopyAttributeValue( element, attribute, &value ) != kAXErrorSuccess ) return @"";
    NSString* result = CFGetTypeID( value ) == CFStringGetTypeID() ? [(NSString*)value copy] : [@"" copy];
    CFRelease( value );
    return [result autorelease];
}

static bool visibleButton( AXUIElementRef application, AXUIElementRef button )
{
    CFTypeRef hidden = nullptr;
    if( AXUIElementCopyAttributeValue( button, CFSTR("AXHidden"), &hidden ) == kAXErrorSuccess )
    {
        bool value = CFGetTypeID( hidden ) == CFBooleanGetTypeID() && CFBooleanGetValue( (CFBooleanRef)hidden );
        CFRelease( hidden ); if( value ) return false;
    }
    CFTypeRef position = nullptr, size = nullptr;
    CGPoint point; CGSize dimensions;
    bool geometry = AXUIElementCopyAttributeValue( button, kAXPositionAttribute, &position ) == kAXErrorSuccess
        && AXUIElementCopyAttributeValue( button, kAXSizeAttribute, &size ) == kAXErrorSuccess
        && CFGetTypeID( position ) == AXValueGetTypeID() && CFGetTypeID( size ) == AXValueGetTypeID()
        && AXValueGetValue( (AXValueRef)position, static_cast<AXValueType>( kAXValueCGPointType ), &point )
        && AXValueGetValue( (AXValueRef)size, static_cast<AXValueType>( kAXValueCGSizeType ), &dimensions );
    if( position ) CFRelease( position ); if( size ) CFRelease( size );
    if( !geometry || dimensions.width <= 0 || dimensions.height <= 0 ) return false;
    AXUIElementRef hit = nullptr;
    if( AXUIElementCopyElementAtPosition( application, point.x + dimensions.width / 2,
                                        point.y + dimensions.height / 2, &hit ) != kAXErrorSuccess ) return false;
    bool visible = false;
    for( int depth = 0; hit && depth < 8; ++depth )
    {
        if( CFEqual( hit, button ) ) { visible = true; break; }
        CFTypeRef parent = nullptr;
        AXUIElementCopyAttributeValue( hit, kAXParentAttribute, &parent ); CFRelease( hit ); hit = nullptr;
        if( parent && CFGetTypeID( parent ) == AXUIElementGetTypeID() ) hit = (AXUIElementRef)parent;
        else if( parent ) CFRelease( parent );
    }
    if( hit ) CFRelease( hit );
    return visible;
}

int main( int argc, char** argv )
{
    @autoreleasepool
    {
        if( argc == 2 && strcmp( argv[1], "capabilities" ) == 0 )
        {
            emit( @{ @"schemaVersion": @1, @"accessibilityTrusted": @((bool)AXIsProcessTrusted()),
                @"screenCaptureAllowed": @(CGPreflightScreenCaptureAccess()), @"permissionsChanged": @NO } );
            return 0;
        }
        if( argc != 3 ) return 2;
        if( strcmp( argv[1], "inspect" ) != 0 && strcmp( argv[1], "press" ) != 0 && strcmp( argv[1], "reveal" ) != 0 ) return 2;
        if( !AXIsProcessTrusted() )
        {
            emit( @{ @"schemaVersion": @1, @"status": @"accessibility_unavailable", @"permissionsChanged": @NO } );
            return 3;
        }
        NSData* requestBytes = [NSData dataWithContentsOfFile:[NSString stringWithUTF8String:argv[2]]];
        if( !requestBytes || [requestBytes length] > 16384 ) return 2;
        id request = [NSJSONSerialization JSONObjectWithData:requestBytes options:0 error:nil];
        if( ![request isKindOfClass:[NSDictionary class]] || [request[@"schemaVersion"] intValue] != 1
            || ![request[@"processId"] isKindOfClass:[NSNumber class]]
            || ![request[@"startSeconds"] isKindOfClass:[NSNumber class]]
            || ![request[@"startMicroseconds"] isKindOfClass:[NSNumber class]]
            || ![request[@"bootId"] isKindOfClass:[NSString class]]
            || ![request[@"executable"] isKindOfClass:[NSString class]] ) return 2;
        long long parsed = [request[@"processId"] longLongValue];
        if( parsed <= 0 || parsed > INT_MAX ) return 2;
        const pid_t pid = static_cast<pid_t>( parsed );
        auto matchesProcess = [&]() -> bool
        {
            proc_bsdinfo info = {};
            char path[PROC_PIDPATHINFO_MAXSIZE] = {}, boot[128] = {};
            size_t size = sizeof( boot );
            return proc_pidinfo( pid, PROC_PIDTBSDINFO, 0, &info, sizeof( info ) ) == sizeof( info )
                && proc_pidpath( pid, path, sizeof( path ) ) > 0
                && sysctlbyname( "kern.bootsessionuuid", boot, &size, nullptr, 0 ) == 0
                && info.pbi_start_tvsec == [request[@"startSeconds"] unsignedLongLongValue]
                && info.pbi_start_tvusec == [request[@"startMicroseconds"] unsignedLongLongValue]
                && [request[@"executable"] isEqualToString:[NSString stringWithUTF8String:path]]
                && [request[@"bootId"] caseInsensitiveCompare:[NSString stringWithUTF8String:boot]] == NSOrderedSame;
        };
        if( !matchesProcess() )
        { emit( @{ @"schemaVersion": @1, @"status": @"process_identity_mismatch" } ); return 7; }
        AXUIElementRef application = AXUIElementCreateApplication( pid );
        AXUIElementSetMessagingTimeout( application, 2.0 );
        std::vector<AXUIElementRef> queue = { application }, matches;
        NSMutableArray* controls = [NSMutableArray array];
        NSString* wanted = [request[@"title"] isKindOfClass:[NSString class]] ? request[@"title"] : nil;
        size_t cursor = 0;
        while( cursor < queue.size() && queue.size() <= 4096 )
        {
            AXUIElementRef element = queue[cursor++];
            NSString* role = textAttribute( element, kAXRoleAttribute );
            // Menus are not window buttons. Avoid traversing their large trees
            // for every readiness observation of an editor's caption action.
            if( [role isEqualToString:(NSString*)kAXMenuBarRole] || [role isEqualToString:(NSString*)kAXMenuRole] ) continue;
            if( [role isEqualToString:(NSString*)kAXButtonRole] )
            {
                NSString* title = textAttribute( element, kAXTitleAttribute );
                if( ![title length] ) title = textAttribute( element, kAXDescriptionAttribute );
                CFTypeRef enabledValue = nullptr;
                bool enabled = AXUIElementCopyAttributeValue( element, kAXEnabledAttribute, &enabledValue ) == kAXErrorSuccess
                    && CFGetTypeID( enabledValue ) == CFBooleanGetTypeID() && CFBooleanGetValue( (CFBooleanRef)enabledValue );
                if( enabledValue ) CFRelease( enabledValue );
                bool relevant = !wanted || [title isEqualToString:wanted];
                bool visible = relevant && visibleButton( application, element );
                if( relevant ) [controls addObject:@{ @"role": role, @"title": title, @"enabled": @(enabled), @"visible": @(visible) }];
                if( wanted && relevant && enabled && ( visible || strcmp( argv[1], "reveal" ) == 0 ) ) matches.push_back( element );
            }
            CFTypeRef children = nullptr;
            if( AXUIElementCopyAttributeValue( element, kAXChildrenAttribute, &children ) == kAXErrorSuccess )
            {
                if( CFGetTypeID( children ) == CFArrayGetTypeID() )
                    for( CFIndex index = 0; index < CFArrayGetCount( (CFArrayRef)children ) && queue.size() <= 4096; ++index )
                    {
                        CFTypeRef child = CFArrayGetValueAtIndex( (CFArrayRef)children, index );
                        if( CFGetTypeID( child ) == AXUIElementGetTypeID() )
                        { CFRetain( child ); queue.push_back( (AXUIElementRef)child ); }
                    }
                CFRelease( children );
            }
        }
        int result = 0;
        if( queue.size() > 4096 )
        { emit( @{ @"schemaVersion": @1, @"status": @"tree_limit_exceeded" } ); result = 4; }
        else if( strcmp( argv[1], "inspect" ) == 0 )
        {
            NSMutableArray* windows = [NSMutableArray array];
            CFArrayRef list = CGWindowListCopyWindowInfo( kCGWindowListOptionOnScreenOnly | kCGWindowListExcludeDesktopElements, kCGNullWindowID );
            for( NSDictionary* window in (NSArray*)list )
                if( [window[(NSString*)kCGWindowOwnerPID] intValue] == pid && [window[(NSString*)kCGWindowLayer] intValue] == 0 )
                    [windows addObject:@{ @"id": window[(NSString*)kCGWindowNumber],
                        @"title": window[(NSString*)kCGWindowName] ?: @"" }];
            if( list ) CFRelease( list );
            emit( @{ @"schemaVersion": @1, @"status": @"observed", @"processId": @(pid),
                @"buttons": controls, @"windows": windows } );
        }
        else if( matches.size() != 1 )
        { emit( @{ @"schemaVersion": @1, @"status": @"no_unique_enabled_target", @"matches": @(matches.size()) } ); result = 5; }
        else if( !matchesProcess() )
        { emit( @{ @"schemaVersion": @1, @"status": @"process_identity_mismatch" } ); result = 7; }
        else if( strcmp( argv[1], "reveal" ) == 0 )
        {
            CFTypeRef window = nullptr;
            AXError error = AXUIElementCopyAttributeValue( matches[0], kAXWindowAttribute, &window );
            if( error == kAXErrorSuccess && window && CFGetTypeID( window ) == AXUIElementGetTypeID() )
            {
                [[NSRunningApplication runningApplicationWithProcessIdentifier:pid] activateWithOptions:NSApplicationActivateIgnoringOtherApps];
                error = AXUIElementPerformAction( (AXUIElementRef)window, kAXRaiseAction );
            }
            else error = kAXErrorNoValue;
            if( window ) CFRelease( window );
            emit( @{ @"schemaVersion": @1, @"status": error == kAXErrorSuccess ? @"window_raised" : @"raise_failed", @"error": @(error) } );
            result = error == kAXErrorSuccess ? 0 : 6;
        }
        else if( !visibleButton( application, matches[0] ) )
        { emit( @{ @"schemaVersion": @1, @"status": @"target_not_visible" } ); result = 5; }
        else
        {
            AXError error = AXUIElementPerformAction( matches[0], kAXPressAction );
            emit( @{ @"schemaVersion": @1, @"status": error == kAXErrorSuccess ? @"action_sent" : @"action_failed", @"error": @(error) } );
            result = error == kAXErrorSuccess ? 0 : 6;
        }
        for( AXUIElementRef element : queue ) CFRelease( element );
        return result;
    }
}
