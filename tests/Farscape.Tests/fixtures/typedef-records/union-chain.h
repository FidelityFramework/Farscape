/* A field typed through a qualified typedef link to a tagged union. */
typedef union fx_ulink
{
  long whole;
  double real;
} fx_ulink_t;

typedef const fx_ulink_t fx_ulink_alias_t;

struct fx_uchain_holder
{
  char tag;
  fx_ulink_alias_t link;
};

void fx_uchain_hold(struct fx_uchain_holder *holder);
