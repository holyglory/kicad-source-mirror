// Synthetic DLL fixture using the real KiCad export/import wrapper.
#include <json_common.h>

extern "C" KICOMMON_API int fixture_touch()
{
    nlohmann::json value = { { "schemaVersion", 1 }, { "status", "available" } };
    nlohmann::json copy = value;
    value = copy;
    return static_cast<int>( value.size() );
}
