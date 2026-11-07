/* A field typed by a typedef of a tagged record no pilot declaration defines. */
#include "far-node.h"

typedef struct fx_far_node fx_far_t;

struct fx_far_holder
{
  fx_far_t node;
};

void fx_far_hold(struct fx_far_holder *holder);
