"""Reject private planning identifiers in a built site's static text.

Run: python -m vouchfx_site_tools.check_private_identifiers SITE_DIR
This standard-library scan does not fetch frames or execute CSS/JavaScript.
"""
from __future__ import annotations

import json
import re
import sys
from collections.abc import Iterator
from html import unescape
from html.parser import HTMLParser
from pathlib import Path


class PrivateIdentifierError(Exception):
    """A fixed diagnostic that contains no matching content or input path."""


TEXT_LIKE_SUFFIXES = {".html", ".js", ".json", ".xml", ".txt"}


def iter_text_like_files(site_dir: Path) -> Iterator[Path]:
    for path in site_dir.rglob("*"):
        if path.is_file() and path.suffix.lower() in TEXT_LIKE_SUFFIXES:
            yield path


_PRIVATE_IDENTIFIER = re.compile(r"\b(?:REQ-[0-9]+|EDGE-[0-9]+|S[0-9]+-[A-Z]-[0-9]+)\b")
_PUBLICATION_JSON_MAX_DEPTH = 128
_PUBLICATION_JSON_MAX_NODES = 100_000
_PUBLIC_SCHEMA_DIALECTS = (
    "https://json-schema.org/draft/2020-12/schema",
    "https://json-schema.org/draft/2020-12/schema#",
)
_SCHEMA_MAP_KEYWORDS = frozenset({"$defs", "properties", "patternProperties", "dependentSchemas"})
_SCHEMA_ARRAY_KEYWORDS = frozenset({"allOf", "anyOf", "oneOf", "prefixItems"})
_SCHEMA_SINGLE_KEYWORDS = frozenset({
    "items", "contains", "additionalProperties", "unevaluatedProperties",
    "unevaluatedItems", "propertyNames", "not", "if", "then", "else", "contentSchema",
})

# Character counts apply after decoding each HTML document. Shared work counts
# every embedded document again: many siblings cannot evade a nesting limit.
_PUBLICATION_HTML_MAX_CHARS = 8 * 1024 * 1024
_PUBLICATION_EMBEDDED_MAX_CHARS = 1024 * 1024
_PUBLICATION_HTML_MAX_DEPTH = 8
_PUBLICATION_HTML_MAX_WORK = 32 * 1024 * 1024
_PUBLICATION_HTML_MAX_DOCUMENTS = 16_384


class _PublicationHTMLBudget:
    """One bounded work allowance shared by a file and its embedded documents."""

    def __init__(self) -> None:
        self.characters = 0
        self.documents = 0

    def document(self, depth: int) -> None:
        self.documents += 1
        if depth > _PUBLICATION_HTML_MAX_DEPTH or self.documents > _PUBLICATION_HTML_MAX_DOCUMENTS:
            raise ValueError("publication HTML limit")

    def consume(self, characters: int) -> None:
        self.characters += characters
        if self.characters > _PUBLICATION_HTML_MAX_WORK:
            raise ValueError("publication HTML limit")


class _PublishedText(HTMLParser):
    """Static text, including literal contexts and isolated embedded documents.

    Reuse HTMLParser's tokenizer and CDATA machinery, but distinguish RCDATA
    entity decoding from raw text, and retain literal content at EOF. This is
    a bounded publication scan, not CSS/JavaScript or browser layout execution.
    """

    _RCDATA = frozenset({"textarea", "title"})
    _LITERAL = _RCDATA | frozenset({"xmp", "plaintext"})
    _TEXT_END_TAGS = _LITERAL | frozenset({"script", "style"})
    CDATA_CONTENT_ELEMENTS = ("script", "style", "textarea", "title", "xmp", "plaintext")

    _BLOCKS = frozenset({
        "address", "article", "aside", "blockquote", "body", "br", "caption",
        "dd", "details", "dialog", "div", "dl", "dt", "fieldset", "figcaption",
        "figure", "footer", "form", "h1", "h2", "h3", "h4", "h5", "h6",
        "head", "header", "hr", "html", "iframe", "legend", "li", "main", "nav", "ol",
        "option", "optgroup", "plaintext", "textarea", "xmp",
        "p", "pre", "section", "summary", "table", "tbody", "td", "tfoot",
        "th", "thead", "title", "tr", "ul",
    })

    _TEXT_INPUT_TYPES = frozenset({"text", "search", "tel", "url", "email"})
    _BUTTON_INPUT_TYPES = frozenset({"submit", "reset", "button"})
    _INPUT_TYPES = _TEXT_INPUT_TYPES | _BUTTON_INPUT_TYPES | frozenset({
        "hidden", "password", "date", "month", "week", "time", "datetime-local",
        "number", "range", "color", "checkbox", "radio", "file", "image",
    })
    _PLACEHOLDER_INPUT_TYPES = _TEXT_INPUT_TYPES | frozenset({"password", "number"})

    def __init__(self, *, xml: bool = False, budget: _PublicationHTMLBudget | None = None,
                 depth: int = 0, data: bool = False, decoded: bool = False) -> None:
        super().__init__(convert_charrefs=not decoded)
        self.chunks: list[str] = []
        self.labels: list[str] = []
        self.ignored = 0
        self.xml = xml
        self.data = data
        self.budget = budget if budget is not None else _PublicationHTMLBudget()
        self.depth = depth
        self.characters = 0
        self.embedded_identifier = False
        self.at_eof = False
        self.budget.document(depth)

    def feed(self, data: str) -> None:
        self.characters += len(data)
        limit = _PUBLICATION_EMBEDDED_MAX_CHARS if self.depth else _PUBLICATION_HTML_MAX_CHARS
        if self.characters > limit:
            raise ValueError("publication HTML limit")
        self.budget.consume(len(data))
        super().feed(data)

    def set_cdata_mode(self, elem: str) -> None:
        super().set_cdata_mode(elem)
        if self.data:
            # JSON strings remain data during optional markup projection too:
            # script/style spelling cannot suppress inline-tag-split text.
            self.clear_cdata_mode()
        elif not self.xml and elem in self._TEXT_END_TAGS:
            # HTML plaintext has no closing tag. Appropriate literal end tags
            # require an exact name followed by HTML whitespace, '/' or '>'.
            self.interesting = re.compile(
                r"(?!)" if elem == "plaintext" else r"</" + re.escape(elem) + r"(?=[\t\n\f\r />])",
                re.IGNORECASE,
            )
        elif self.xml:
            # XML script/style/title names have ordinary character data too;
            # HTML raw-text tokenisation must not hide their CDATA sections.
            self.clear_cdata_mode()

    def parse_endtag(self, i: int) -> int:
        if self.data:
            # Ordinary closing tags keep inline adjacency. Tolerant malformed
            # end tags can discard DATA; refuse that unsupported syntax.
            end = self.rawdata.find(">", i + 2)
            if end < 0:
                if self.at_eof:
                    raise ValueError("publication DATA end tag")
                return -1
            closing = self.rawdata[i:end + 1]
            if closing != "</>" and not re.fullmatch(r"</\s*[a-zA-Z][-.a-zA-Z0-9:_]*\s*>", closing):
                raise ValueError("publication DATA end tag")
        if not self.xml and self.cdata_elem in self._TEXT_END_TAGS:
            # The tokenizer reached an appropriate end-tag prefix. Attributes
            # are ignored on end tags; quoted '>' still belongs to the tag.
            quote = None
            before_value = unquoted_value = False
            for end in range(i + 2 + len(self.cdata_elem), len(self.rawdata)):
                char = self.rawdata[end]
                if quote:
                    if char == quote:
                        quote = None
                elif char == ">":
                    self.handle_endtag(self.cdata_elem)
                    self.clear_cdata_mode()
                    return end + 1
                elif before_value:
                    if char not in "\t\n\f\r ":
                        before_value = False
                        if char in {"'", '"'}:
                            quote = char
                        else:
                            unquoted_value = True
                elif unquoted_value:
                    if char in "\t\n\f\r ":
                        unquoted_value = False
                elif char == "=":
                    before_value = True
            return -1
        return super().parse_endtag(i)

    def goahead(self, end: bool) -> None:
        self.at_eof = end
        try:
            super().goahead(end)
        finally:
            self.at_eof = False
        if end and not self.xml and self.cdata_elem in self._LITERAL and self.rawdata:
            self.handle_data(self.rawdata)
            self.updatepos(0, len(self.rawdata))
            self.rawdata = ""

    def parse_marked_section(self, i: int, report: int = 1) -> int:
        if not self.data:
            return super().parse_marked_section(i, report)
        # DATA wrappers have structural syntax, never source exemptions. Do
        # not let an incomplete/unsupported header enter literal EOF fallback.
        raw = self.rawdata
        name = re.match(r"[a-zA-Z][-_.a-zA-Z0-9]*", raw[i + 3:])
        if name is None:
            if i + 3 == len(raw) and not self.at_eof:
                return -1
            raise ValueError("publication DATA marked header")
        cursor = i + 3 + name.end()
        if cursor == len(raw):
            if not self.at_eof:
                return -1
            raise ValueError("publication DATA marked header")
        family = name.group().lower()
        conditional = family in {"if", "else", "endif"}
        if family in {"cdata", "ignore", "include", "rcdata", "temp"}:
            while cursor < len(raw) and raw[cursor].isspace():
                cursor += 1
            if cursor == len(raw) and not self.at_eof:
                return -1
            if cursor == len(raw) or raw[cursor] != "[":
                raise ValueError("publication DATA marked header")
            body_start = cursor + 1
            closing = re.search(r"]\s*]\s*>", raw[body_start:])
        elif conditional:
            if not raw[cursor].isspace() and raw[cursor] not in "[]":
                raise ValueError("publication DATA marked header")
            body_start = cursor
            closing = re.search(r"]\s*>", raw[body_start:])
        else:
            raise ValueError("publication DATA marked header")
        if closing is None:
            if not self.at_eof:
                return -1
            body_end = result = len(raw)
        else:
            body_end = body_start + closing.start()
            result = body_start + closing.end()
        if report:
            projected = _PublishedText(budget=self.budget, depth=self.depth + 1,
                                       data=True, decoded=not self.convert_charrefs)
            projected.feed(raw[body_start:body_end])
            projected.close()
            # Conditional marker names are syntax, not adjacent prose. Their
            # condition text is still DATA. Standard bodies keep adjacency.
            if conditional:
                self.chunks.append("\n")
            self.chunks.extend(projected.chunks)
            if conditional:
                self.chunks.append("\n")
            self.embedded_identifier |= projected.contains_identifier()
        return result

    def parse_starttag(self, i: int) -> int:
        result = super().parse_starttag(i)
        if result < 0 and self.data and self.at_eof:
            # A marked terminator can split a quoted child attribute. Refuse
            # unfinished DATA markup instead of suppressing inline projection.
            raise ValueError("publication DATA start tag")
        return result

    def handle_startendtag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        # A self-closing slash does not close non-void HTML literal elements.
        if not self.xml and not self.data and tag in self.CDATA_CONTENT_ELEMENTS:
            self.handle_starttag(tag, attrs)
            self.set_cdata_mode(tag)
        else:
            super().handle_startendtag(tag, attrs)

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        if self.data and not self.convert_charrefs:
            # HTMLParser unescapes attributes even with convert_charrefs=False.
            # Recover its original attribute tokens when the enclosing DATA
            # fragment has already received its one entity decode.
            raw = self.get_starttag_text()
            prefix = re.match(r"<[a-zA-Z][^\t\n\r\f />\x00]*(?:\s|/(?!>))*", raw or "")
            if prefix is None:
                raise ValueError("publication DATA attribute")
            position = prefix.end()
            original_attrs = []
            attribute = re.compile(
                r"((?<=[\'\"\s/])[^\s/>][^\s/=>]*)(\s*=+\s*(\'[^\']*\'|\"[^\"]*\"|(?![\'\"])[^>\s]*))?(?:\s|/(?!>))*"
            )
            while match := attribute.match(raw, position):
                name, assignment, value = match.group(1, 2, 3)
                if not assignment:
                    value = None
                elif value[:1] in {"'", '"'} and value[-1:] == value[:1]:
                    value = value[1:-1]
                original_attrs.append((name.lower(), value))
                position = match.end()
            if [name for name, _ in original_attrs] != [name for name, _ in attrs]:
                raise ValueError("publication DATA attribute")
            attrs = original_attrs
        if not self.xml and not self.data and tag in {"script", "style"}:
            self.ignored += 1
        if self.ignored:
            return
        if self.xml or tag in self._BLOCKS:
            self.chunks.append("\n")
        if self.data:
            # Every serialized attribute string is JSON data, including
            # opaque/custom and duplicate attributes. Attribute decoding has
            # already occurred once; project its inline markup without another
            # entity decode or concatenation with another attribute/document.
            for name, value in attrs:
                for datum in (name, value):
                    if datum is None:
                        continue
                    if _PRIVATE_IDENTIFIER.search(datum):
                        self.embedded_identifier = True
                    else:
                        projected = _PublishedText(budget=self.budget, depth=self.depth + 1,
                                                   data=True, decoded=True)
                        projected.feed(datum)
                        projected.close()
                        self.embedded_identifier |= projected.contains_identifier()
            return
        # Duplicate HTML attributes use the first occurrence, including an
        # empty first srcdoc. Do not let a later duplicate change its context.
        attributes: dict[str, str | None] = {}
        for name, value in attrs:
            attributes.setdefault(name, value)
        visible_attrs: set[str] = set()
        if not self.xml and tag == "input":
            # Enumerated HTML types are case-insensitive; absent/invalid values
            # mean text. Do not trim whitespace into a different valid state.
            input_type = (attributes.get("type") or "text").lower()
            if input_type not in self._INPUT_TYPES:
                input_type = "text"
            if input_type in self._TEXT_INPUT_TYPES | self._BUTTON_INPUT_TYPES:
                visible_attrs.add("value")
            if input_type in self._PLACEHOLDER_INPUT_TYPES:
                visible_attrs.add("placeholder")
        elif not self.xml and tag == "textarea":
            visible_attrs.add("placeholder")
        elif not self.xml and tag in {"option", "optgroup", "track"}:
            visible_attrs.add("label")
        self.labels.extend(
            value for name, value in attributes.items() if value is not None
            and (name in {"alt", "title", "aria-label"} or (tag == "meta" and name == "content")
                 or name in visible_attrs)
        )
        if not self.xml and tag == "iframe" and "srcdoc" in attributes:
            embedded = _PublishedText(budget=self.budget, depth=self.depth + 1, data=self.data)
            embedded.feed(attributes["srcdoc"] or "")
            embedded.close()
            self.embedded_identifier |= embedded.contains_identifier()

    def handle_endtag(self, tag: str) -> None:
        if not self.xml and not self.data and tag in {"script", "style"}:
            self.ignored = max(0, self.ignored - 1)
        if not self.ignored and (self.xml or tag in self._BLOCKS):
            self.chunks.append("\n")

    def handle_data(self, data: str) -> None:
        if not self.ignored:
            self.chunks.append(unescape(data) if not self.xml and self.cdata_elem in self._RCDATA else data)

    def handle_entityref(self, name: str) -> None:
        # Already-decoded DATA fragments retain a literal reference spelling;
        # dropping it could invent an identifier across unrelated characters.
        self.handle_data("&" + name + ";")

    def handle_charref(self, name: str) -> None:
        self.handle_data("&#" + name + ";")

    def parse_html_declaration(self, i: int) -> int:
        if self.data and not self.at_eof and self.rawdata[i:] in {"<!", "<!-"}:
            # These prefixes can still become a marked section or comment in
            # the next feed. Preserve them until their syntax is classified.
            return -1
        if self.data and not self.rawdata.startswith(("<!--", "<!["), i):
            # JSON declaration names have no source meaning. Retain every body
            # through ordinary bounded tokenization, including at EOF.
            self.chunks.append("\n")
            return i + 2
        return super().parse_html_declaration(i)

    def parse_pi(self, i: int) -> int:
        if self.data:
            self.chunks.append("\n")
            return i + 2
        return super().parse_pi(i)

    def parse_bogus_comment(self, i: int, report: int = 1) -> int:
        if self.data:
            self.chunks.append("\n")
            return i + 2
        return super().parse_bogus_comment(i, report)

    def parse_comment(self, i: int, report: int = 1) -> int:
        if self.data:
            # JSON comment spellings are data, including an unterminated one.
            # Let the ordinary tokenizer project their body; the opener is a
            # boundary, never a source exemption or join with preceding prose.
            self.chunks.append("\n")
            return i + 4
        return super().parse_comment(i, report)

    def unknown_decl(self, declaration: str) -> None:
        if self.xml:
            if not declaration.startswith("CDATA["):
                raise ValueError("publication XML marked section")
            # CDATA is literal XML character data, contiguous with text on
            # either side. Its markup/entity spellings are not decoded again.
            self.handle_data(declaration[6:])
        elif self.data:
            # Every DATA marked section is handled structurally above. Never
            # grant a new inherited declaration path a silent fallback.
            raise ValueError("publication DATA marked declaration")

    def contains_identifier(self) -> bool:
        return self.embedded_identifier or bool(_PRIVATE_IDENTIFIER.search("".join(self.chunks))) or any(
            _PRIVATE_IDENTIFIER.search(label) for label in self.labels
        )


class _PublicationJSONObject(tuple):
    """Keep every JSON object member, including repeated names, distinct from arrays."""


def _publication_json_members(value: _PublicationJSONObject | dict) -> Iterator[tuple[str, object]]:
    return iter(value.items()) if isinstance(value, dict) else iter(value)


def _publication_is_schema(value: object, *, inherited: bool = False) -> bool:
    if not isinstance(value, (_PublicationJSONObject, dict)):
        return False
    declarations = [child for key, child in _publication_json_members(value) if key == "$schema"]
    # An ambiguous declaration cannot grant a DATA value the annotation exception.
    return (inherited or bool(declarations)) and all(child in _PUBLIC_SCHEMA_DIALECTS for child in declarations)


def _publication_json_strings(
    value: object, *, descriptions_only: bool, schema_document: bool = False,
) -> Iterator[str]:
    """Bound traversal; exempt comments only at recognised schema locations.

    A declared 2020-12 schema has subschemas in its standard applicator/definition
    keywords. Examples/defaults/unknown extensions are data, even inside a schema.
    Source description-only scans need no comment exemption: they never emit
    other string values in the first place.
    """
    pending = [(value, 0, "schema" if schema_document else "data")]
    visited = 0
    while pending:
        node, depth, context = pending.pop()
        visited += 1
        if depth > _PUBLICATION_JSON_MAX_DEPTH or visited > _PUBLICATION_JSON_MAX_NODES:
            raise ValueError("publication JSON limit")
        if isinstance(node, (_PublicationJSONObject, dict)):
            if context == "schema" and not _publication_is_schema(node, inherited=True):
                context = "data"
            for key, child in _publication_json_members(node):
                if descriptions_only and key == "description" and isinstance(child, str):
                    yield child
                if not descriptions_only:
                    yield key
                child_context = "data"
                if context == "schema" and key == "$comment" and isinstance(child, str):
                    # Still visit exempt annotations to charge JSON depth/work.
                    child_context = "annotation"
                elif context == "schema-map":
                    child_context = "schema"
                elif context == "schema":
                    if key in _SCHEMA_MAP_KEYWORDS:
                        child_context = "schema-map"
                    elif key in _SCHEMA_ARRAY_KEYWORDS:
                        child_context = "schema-array"
                    elif key in _SCHEMA_SINGLE_KEYWORDS:
                        child_context = "schema"
                pending.append((child, depth + 1, child_context))
        elif isinstance(node, list):
            child_context = "schema" if context == "schema-array" else "data"
            pending.extend((child, depth + 1, child_context) for child in node)
        elif isinstance(node, str) and not descriptions_only and context != "annotation":
            yield node


def check_private_identifiers(site_dir: Path) -> None:
    """Reject internal task labels in page text, search JSON and plain output.

    Only actual HTML source comments and script/style bodies receive exemptions;
    their spellings inside literal text remain visible. Srcdoc is an independent
    bounded HTML document, never concatenated with its parent or siblings.
    JSON keys/values are data; only recognised schema annotation values are exempt.
    Inline markup/entities cannot hide an identifier; block boundaries remain spaces.
    JavaScript source is outside this prose check, as are repository comments.
    Diagnostics deliberately contain neither matched content nor input paths.
    """
    try:
        if not site_dir.is_dir():
            raise PrivateIdentifierError("Published text could not be read or parsed safely.")
    except (OSError, ValueError) as exc:
        raise PrivateIdentifierError("Published text could not be read or parsed safely.") from exc
    for file in iter_text_like_files(site_dir):
        suffix = file.suffix.lower()
        if suffix == ".js":
            continue
        try:
            with file.open(encoding="utf-8") as reader:
                text = reader.read(_PUBLICATION_HTML_MAX_CHARS + 1)
            if len(text) > _PUBLICATION_HTML_MAX_CHARS:
                raise ValueError("publication text limit")
            budget = _PublicationHTMLBudget()
            if suffix == ".json":
                value = json.loads(text, object_pairs_hook=_PublicationJSONObject)
                is_schema = _publication_is_schema(value)
                values = _publication_json_strings(value, descriptions_only=False, schema_document=is_schema)
            else:
                values = (text,)
            for value in values:
                # JSON strings/keys are data, including literal comment/script
                # spellings. Check them before optional rendered-text analysis.
                leaked = suffix in {".json", ".txt"} and bool(_PRIVATE_IDENTIFIER.search(unescape(value)))
                if not leaked and suffix != ".txt":
                    parser = _PublishedText(xml=suffix == ".xml", budget=budget, data=suffix == ".json")
                    parser.feed(value)
                    parser.close()
                    leaked = parser.contains_identifier()
                if leaked:
                    raise PrivateIdentifierError("Published text contains an internal planning identifier.")
        except (OSError, UnicodeError, ValueError, RecursionError, AssertionError) as exc:
            raise PrivateIdentifierError("Published text could not be read or parsed safely.") from exc


def main(argv: list[str] | None = None) -> int:
    arguments = sys.argv[1:] if argv is None else argv
    if len(arguments) != 1:
        print("usage: python -m vouchfx_site_tools.check_private_identifiers SITE_DIR", file=sys.stderr)
        return 2
    try:
        site_dir = Path(arguments[0])
        if not site_dir.is_dir():
            raise PrivateIdentifierError("Published text could not be read or parsed safely.")
        check_private_identifiers(site_dir)
    except PrivateIdentifierError as failure:
        print(f"Private identifier check failed: {failure}", file=sys.stderr)
        return 1
    except (OSError, UnicodeError, ValueError):
        print("Private identifier check failed: Published text could not be read or parsed safely.", file=sys.stderr)
        return 1
    print("Private identifier check passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
