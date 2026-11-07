/* A qualified typedef chain to a tagged union no pilot declaration defines. */
#include "far-union.h"

typedef union fx_far_u fx_far_u_t;
typedef const fx_far_u_t fx_far_u_alias_t;

struct fx_far_u_holder
{
  fx_far_u_alias_t link;
};

void fx_far_u_hold(struct fx_far_u_holder *holder);
