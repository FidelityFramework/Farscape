/* Measures a record whose field typedef names a record this header only includes. */
#include "node-only.h"

typedef struct fx_shared_node fx_shared_t;

struct fx_user
{
  fx_shared_t node;
};

void fx_use_node(struct fx_user *user);
