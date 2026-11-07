/* A field typed through two typedef links to a tagged record. The second link is qualified
   (`const fx_link_t`), so the unit's typedef map does not flatten it: the resolver itself must
   follow every link. */
typedef struct fx_link
{
  long first;
  long second;
} fx_link_t;

typedef const fx_link_t fx_link_alias_t;

struct fx_chain_holder
{
  char tag;
  fx_link_alias_t link;
};

void fx_chain_hold(struct fx_chain_holder *holder);
