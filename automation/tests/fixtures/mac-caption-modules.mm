#import <AppKit/AppKit.h>
#include <dlfcn.h>
#include <stdio.h>

int main(int argc, char** argv)
{
    if(argc != 3) return 1;
    @autoreleasepool
    {
        [NSApplication sharedApplication]; [NSApp finishLaunching];
        void* first = dlopen(argv[1], RTLD_NOW | RTLD_LOCAL);
        void* second = dlopen(argv[2], RTLD_NOW | RTLD_LOCAL);
        if(!first || !second) return 2;
        using Factory = NSButton* (*)(NSWindow*, int*);
        auto a = (Factory)dlsym(first, "createCaption"), b = (Factory)dlsym(second, "createCaption");
        if(!a || !b) return 3;
        NSWindow* window = [[NSWindow alloc] initWithContentRect:NSMakeRect(0,0,600,400)
            styleMask:NSWindowStyleMaskTitled backing:NSBackingStoreBuffered defer:NO];
        int countA=0, countB=0;
        NSButton* buttonA=a(window,&countA); NSButton* buttonB=b(window,&countB);
        [window makeKeyAndOrderFront:nil]; [window displayIfNeeded];
        [[NSRunLoop currentRunLoop] runUntilDate:[NSDate dateWithTimeIntervalSinceNow:0.02]];
        if([[buttonA target] class] != [[buttonB target] class]) return 4;
        [buttonA performClick:nil]; [buttonB performClick:nil]; [buttonB performClick:nil];
        if(countA != 1 || countB != 2) return 5;
        [window release];
        // Objective-C classes live for the process. Keep their owning modules
        // loaded, as KiCad does for its editor modules.
        puts("{\"schemaVersion\":1,\"sharedClass\":true,\"independentCallbacks\":true}");
        return 0;
    }
}
