// MQL5 fixture: Issue #141 — abstract/final class modifiers
// `abstract` precedes `class` (issue #141 example).
// `final` follows the type name, BEFORE the optional base clause —
// documented placement (MQL5 reference: docs/basis/types/classes,
// #final_class / #final_struct; MQL5Book example: `class Derived final : public Base`).
abstract class CAnimal {
public:
    virtual void Sound();
};

class CFoo final {
};

class CBar final : public CFoo {
};

struct Settings final {
};

class CCat : public CAnimal {
public:
    void Sound() { }
};

// Issue #141 exact scenario: `abstract` + base clause in one declaration.
abstract class CShape : public CObject {
public:
    void F() { }
};

void OnTick() {
    CBar bar;
}
