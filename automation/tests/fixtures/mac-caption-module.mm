#include "caption_action.h"

#ifdef KICAD_DUPLICATE_CLASS_CONTROL
@interface KICAD_DUPLICATE_CLASS_CONTROL_TARGET : NSObject
@end
@implementation KICAD_DUPLICATE_CLASS_CONTROL_TARGET
@end
#endif

extern "C" NSButton* createCaption(NSWindow* window, int* counter)
{
    auto state = KIPLATFORM::UI::AddMacCaptionAction(window, @"Update", [counter] { ++*counter; });
    state(true, true);
    return (NSButton*)[[[window titlebarAccessoryViewControllers] lastObject] view];
}
