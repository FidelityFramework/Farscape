/* By-value types that resolve through no typedef, record or profile entry of this unit. */
#include "elsewhere.h"

struct fx_incomplete;

typedef void (*fx_elsewhere_cb)(struct fx_elsewhere value);

void fx_take_elsewhere(struct fx_elsewhere value);
void fx_take_incomplete(struct fx_incomplete value);
void fx_register(fx_elsewhere_cb cb);
struct fx_elsewhere fx_make_elsewhere(void);
