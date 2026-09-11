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

// Follow one menu level, crossing only its anonymous AXMenu container.
// Never search another application's menu or invoke a hidden descendant.
static AXUIElementRef menuChild( AXUIElementRef parent, NSString* title )
{
    std::vector<AXUIElementRef> queue = { parent }, matches;
    CFRetain( parent );
    for( size_t cursor = 0; cursor < queue.size() && queue.size() <= 1024; ++cursor )
    {
        AXUIElementRef element = queue[cursor];
        if( cursor > 0 && ![textAttribute( element, kAXRoleAttribute ) isEqualToString:(NSString*)kAXMenuRole] )
        {
            NSString* role = textAttribute( element, kAXRoleAttribute );
            if( ([role isEqualToString:(NSString*)kAXMenuItemRole] || [role isEqualToString:(NSString*)kAXMenuBarItemRole])
                && [textAttribute( element, kAXTitleAttribute ) isEqualToString:title] ) matches.push_back( element );
            continue;
        }
        CFTypeRef children = nullptr;
        if( AXUIElementCopyAttributeValue( element, kAXChildrenAttribute, &children ) == kAXErrorSuccess )
        {
            if( CFGetTypeID( children ) == CFArrayGetTypeID() )
                for( CFIndex index = 0; index < CFArrayGetCount( (CFArrayRef)children ) && queue.size() <= 1024; ++index )
                {
                    CFTypeRef child = CFArrayGetValueAtIndex( (CFArrayRef)children, index );
                    if( CFGetTypeID( child ) == AXUIElementGetTypeID() )
                    { CFRetain( child ); queue.push_back( (AXUIElementRef)child ); }
                }
            CFRelease( children );
        }
    }
    AXUIElementRef result = matches.size() == 1 && queue.size() <= 1024 ? matches[0] : nullptr;
    if( result ) CFRetain( result );
    for( AXUIElementRef element : queue ) CFRelease( element );
    return result;
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
        if( strcmp( argv[1], "inspect" ) != 0 && strcmp( argv[1], "press" ) != 0
            && strcmp( argv[1], "reveal" ) != 0 && strcmp( argv[1], "menu" ) != 0 ) return 2;
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
        if( strcmp( argv[1], "menu" ) == 0 )
        {
            id path = request[@"menuPath"];
            if( ![path isKindOfClass:[NSArray class]] || [path count] != 2
                || ![path[0] isKindOfClass:[NSString class]] || ![path[1] isKindOfClass:[NSString class]]
                || ![path[0] length] || ![path[1] length] ) { CFRelease( application ); return 2; }
            NSString* title = [request[@"windowTitle"] isKindOfClass:[NSString class]] ? request[@"windowTitle"] : nil;
            CFTypeRef windows = nullptr;
            AXUIElementCopyAttributeValue( application, kAXWindowsAttribute, &windows );
            std::vector<AXUIElementRef> owners;
            if( windows && CFGetTypeID( windows ) == CFArrayGetTypeID() )
                for( CFIndex index = 0; index < CFArrayGetCount( (CFArrayRef)windows ); ++index )
                {
                    AXUIElementRef window = (AXUIElementRef)CFArrayGetValueAtIndex( (CFArrayRef)windows, index );
                    if( CFGetTypeID( window ) == AXUIElementGetTypeID()
                        && [textAttribute( window, kAXTitleAttribute ) isEqualToString:title] ) owners.push_back( window );
                }
            bool raised = false; NSString* focusedTitle = @""; bool frontmost = false;
            AXError raiseError = kAXErrorNoValue, focusError = kAXErrorNoValue, activationError = kAXErrorAttributeUnsupported;
            if( owners.size() == 1 && matchesProcess() )
            {
                [[NSRunningApplication runningApplicationWithProcessIdentifier:pid] activateWithOptions:NSApplicationActivateIgnoringOtherApps];
                Boolean settable = false;
                if( AXUIElementIsAttributeSettable( application, kAXFrontmostAttribute, &settable ) == kAXErrorSuccess && settable )
                    activationError = AXUIElementSetAttributeValue( application, kAXFrontmostAttribute, kCFBooleanTrue );
                settable = false;
                if( AXUIElementIsAttributeSettable( owners[0], kAXMainAttribute, &settable ) == kAXErrorSuccess && settable )
                    AXUIElementSetAttributeValue( owners[0], kAXMainAttribute, kCFBooleanTrue );
                raiseError = AXUIElementPerformAction( owners[0], kAXRaiseAction );
                raised = raiseError == kAXErrorSuccess;
                if( raised )
                {
                    bool focused = false;
                    for( int attempt = 0; attempt < 50 && !focused; ++attempt )
                    {
                        CFTypeRef actual = nullptr;
                        focusError = AXUIElementCopyAttributeValue( application, kAXFocusedWindowAttribute, &actual );
                        CFTypeRef active = nullptr;
                        AXUIElementCopyAttributeValue( application, kAXFrontmostAttribute, &active );
                        frontmost = active && CFGetTypeID( active ) == CFBooleanGetTypeID() && CFBooleanGetValue( (CFBooleanRef)active );
                        if( active ) CFRelease( active );
                        focusedTitle = actual && CFGetTypeID( actual ) == AXUIElementGetTypeID()
                            ? textAttribute( (AXUIElementRef)actual, kAXTitleAttribute ) : @"";
                        focused = actual && CFEqual( actual, owners[0] ) && frontmost;
                        if( actual ) CFRelease( actual );
                        if( !focused ) [[NSRunLoop currentRunLoop] runUntilDate:[NSDate dateWithTimeIntervalSinceNow:0.1]];
                    }
                    raised = focused && matchesProcess();
                }
            }
            if( windows ) CFRelease( windows );
            if( !raised )
            {
                CFRelease( application );
                emit( @{ @"schemaVersion": @1, @"status": @"menu_window_not_ready", @"matches": @(owners.size()),
                    @"requestedTitle": title ?: @"", @"focusedTitle": focusedTitle, @"frontmost": @(frontmost),
                    @"raiseError": @(raiseError), @"focusError": @(focusError), @"activationError": @(activationError) } ); return 5;
            }
            CFTypeRef bar = nullptr;
            AXUIElementCopyAttributeValue( application, kAXMenuBarAttribute, &bar );
            AXUIElementRef parent = bar && CFGetTypeID( bar ) == AXUIElementGetTypeID() ? (AXUIElementRef)bar : nullptr;
            if( bar && !parent ) CFRelease( bar );
            bool sent = false; NSString* failure = @"menu_bar_unavailable"; NSUInteger lastLevel = 0;
            for( NSUInteger level = 0; parent && level < 2; ++level )
            {
                lastLevel = level;
                AXUIElementRef child = nullptr;
                for( int attempt = 0; attempt < 20 && !child; ++attempt )
                {
                    child = menuChild( parent, path[level] );
                    if( child && !visibleButton( application, child ) ) { CFRelease( child ); child = nullptr; }
                    if( !child ) [[NSRunLoop currentRunLoop] runUntilDate:[NSDate dateWithTimeIntervalSinceNow:0.05]];
                }
                CFRelease( parent ); parent = child;
                if( !child ) { failure = @"unique_visible_menu_missing"; break; }
                CFTypeRef enabled = nullptr;
                bool actionable = AXUIElementCopyAttributeValue( child, kAXEnabledAttribute, &enabled ) == kAXErrorSuccess
                    && CFGetTypeID( enabled ) == CFBooleanGetTypeID() && CFBooleanGetValue( (CFBooleanRef)enabled );
                if( enabled ) CFRelease( enabled );
                if( !actionable ) { failure = @"menu_disabled"; break; }
                if( !matchesProcess() ) { failure = @"process_changed"; break; }
                if( !visibleButton( application, child ) ) { failure = @"menu_no_longer_visible"; break; }
                if( AXUIElementPerformAction( child, kAXPressAction ) != kAXErrorSuccess ) { failure = @"menu_press_failed"; break; }
                sent = level == 1;
            }
            if( parent ) CFRelease( parent );
            CFRelease( application );
            emit( @{ @"schemaVersion": @1, @"status": sent ? @"menu_action_sent" : @"menu_not_actionable", @"menuPath": path,
                @"level": @(lastLevel), @"reason": sent ? @"" : failure } );
            return sent ? 0 : 5;
        }
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
