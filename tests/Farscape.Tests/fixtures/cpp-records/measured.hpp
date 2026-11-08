// C++ records carrying an unnamed bit-field or a field whose type reaches an aligned(8) typedef
// (#182), and a record whose only member is an unnamed bit-field (#183). Clang lays each out;
// the parse keeps the unnamed bit-fields and typedef attributes and carries clang's measurement.
typedef int fx_cpp_wide_int __attribute__((aligned(8)));

namespace fx
{
struct base
{
  int x;
};

struct only
{
  int : 8;
};
}

class fx_cpp_pad
{
public:
  char a;
  int : 3;
  char b;
};

struct fx_cpp_wide
{
  char tag;
  fx_cpp_wide_int value;
};

struct fx_cpp_derived : fx::base
{
  char c;
  int : 0;
  char d;
};
