// Isolated native test control. Never part of the shipped updater.
#import <AppKit/AppKit.h>
#include <libproc.h>
#include <sys/proc_info.h>
#include <sys/sysctl.h>
#include <stddef.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

int main( int argc, char** argv )
{
    @autoreleasepool
    {
        if( argc != 3 ) return 2;
        int pid = atoi( argv[2] );
        if( pid <= 0 ) return 2;
        if( strcmp( argv[1], "quit" ) == 0 )
        {
            NSRunningApplication* app = [NSRunningApplication runningApplicationWithProcessIdentifier:pid];
            return app && [app terminate] ? 0 : 3;
        }
        if( strcmp( argv[1], "identity" ) != 0 ) return 2;
        proc_bsdinfo info = {};
        char path[PROC_PIDPATHINFO_MAXSIZE] = {}, boot[128] = {};
        size_t bytes = sizeof( boot );
        if( proc_pidinfo( pid, PROC_PIDTBSDINFO, 0, &info, sizeof( info ) ) != sizeof( info )
            || proc_pidpath( pid, path, sizeof( path ) ) <= 0
            || sysctlbyname( "kern.bootsessionuuid", boot, &bytes, nullptr, 0 ) != 0 ) return 4;
        NSDictionary* identity = @{ @"processId": @(pid), @"bootId": @(boot), @"startSeconds": @(info.pbi_start_tvsec),
            @"startMicroseconds": @(info.pbi_start_tvusec), @"executable": @(path),
            @"structureSize": @(sizeof( info )), @"secondsOffset": @(offsetof( proc_bsdinfo, pbi_start_tvsec )),
            @"microsecondsOffset": @(offsetof( proc_bsdinfo, pbi_start_tvusec )) };
        NSData* data = [NSJSONSerialization dataWithJSONObject:identity options:0 error:nil];
        if( !data ) return 5;
        fwrite( [data bytes], 1, [data length], stdout );
        return 0;
    }
}
