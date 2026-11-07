/* Two typedef links to a tagged record no pilot declaration defines. */
#include "far-node.h"

typedef struct fx_far_node fx_far_link_t;
typedef const fx_far_link_t fx_far_alias_t;

struct fx_far_chain
{
  fx_far_alias_t node;
};

void fx_far_chain_hold(struct fx_far_chain *holder);
