/* Issue #179 fixture: the library header, found through the module's -I path. Its count type
   comes from a header only the module's -isystem path reaches. */
#include <fx_sys_types.h>
fx_sys_count_t fx_sys_count(void);
