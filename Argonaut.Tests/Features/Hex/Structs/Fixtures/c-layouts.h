/* Struct layouts recorded from Clang by record-c-layouts.sh, for StructLayoutCTests: every
   struct here is laid out by the struct decoder for both targets and compared with Clang's
   sizes, alignments and field offsets. No #include - the recording script declares the
   <stdint.h> types for Clang, and the decoder knows them already. */

#define NAME_LENGTH 16
#define DOUBLE_NAME (NAME_LENGTH * 2)
static const int kSlots = 3;
constexpr int kGrid = 4;

typedef unsigned int DWORD;
typedef unsigned short WORD;
typedef unsigned char BYTE;

struct Basic { char c; int i; short s; double d; };
struct Tail { double d; char c; };
struct Chars { char a; signed char b; unsigned char c; char16_t w16; char32_t w32; wchar_t w; };
struct Longs { char c; long l; unsigned long ul; long long ll; unsigned long long ull; long int li; unsigned long int uli; };
struct Pointers { char c; void* p; const char* name; int** pp; void (*callback)(int, char*); };
struct Sizes { char c; size_t size; intptr_t ip; uintptr_t up; ptrdiff_t diff; };
struct FixedWidth { uint8_t a; int16_t b; uint32_t c; int64_t d; bool e; float f; };
struct Arrays { char name[NAME_LENGTH]; int grid[kGrid][kSlots]; short pair[2]; char wide[DOUBLE_NAME + 1]; };
struct Nested { char tag; struct Basic basic; Tail tail[2]; };
typedef struct { BYTE kind; DWORD value; WORD flags; } Header;
typedef struct Tagged { int id; Header header; } Tagged, *PTagged;
struct Holder { Header first; PTagged next; Tagged inline_one; };
struct Outer { struct Inner { short x; double y; } inner; int count; Inner more[2]; };
struct Methods
{
    int a;
    int get() const { return a; }
    Methods() : a(0) {}
    static int s;
    char b;
    void set(int v);
};
class Access { public: int a; private: char b; protected: double c; };
struct Empty {};
struct HasEmpty { char c; Empty e; int i; };
struct Sample { int32_t count; float ratio; bool enabled; char name[256]; };
