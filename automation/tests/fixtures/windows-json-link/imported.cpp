// The manager's other translation units already import JSON from kicommon.
// Without this second consumer, a raw-header-only executable can avoid pulling
// the imported definitions and fail to reproduce the actual mixed-TU link.
#include <json_common.h>

int imported_json_size()
{
    nlohmann::json value = { { "schemaVersion", 1 }, { "status", "available" } };
    nlohmann::json copy = value;
    value = copy;
    return static_cast<int>( value.size() );
}
