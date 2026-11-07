/* The shape of glibc's __pthread_mutex_s.__list: a field typed by a typedef of a tagged record. */
typedef struct fx_node
{
  struct fx_node *prev;
  struct fx_node *next;
} fx_node_t;

struct fx_holder
{
  int lock;
  fx_node_t list;
};

void fx_hold(struct fx_holder *holder);
