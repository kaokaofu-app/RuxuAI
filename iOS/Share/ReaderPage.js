/* Safari invokes this object only when the user opens the share extension. */
var ExtensionPreprocessingJS = {
    run: function (arguments) {
        var limit = 60000;
        var title = document.title ? document.title.trim().slice(0, 220) : "";
        var reply = { title: title, text: "", truncated: false };
        try {
            var selection = window.getSelection();
            if (selection && !selection.isCollapsed) {
                var chosen = selection.toString().trim();
                if (chosen) {
                    reply.text = chosen.slice(0, limit);
                    reply.truncated = chosen.length > limit;
                    reply.selection = true;
                }
            }
            if (!reply.text) {
                var main = document.querySelector("main, [role=main]");
                var articles = (main || document).querySelectorAll("article");
                var root = articles.length === 1 ? articles[0] : (main || document.body);
                if (root) {
                    var excluded = /^(SCRIPT|NOSCRIPT|STYLE|TEMPLATE|FORM|INPUT|TEXTAREA|SELECT|BUTTON|NAV|ASIDE|FOOTER|SVG|CANVAS|IFRAME|VIDEO|AUDIO)$/;
                    var blocks = /^(P|DIV|H[1-6]|SECTION|ARTICLE|LI|UL|OL|TABLE|TR|TD|TH|BLOCKQUOTE|BR|PRE|FIGCAPTION)$/;
                    var output = "", visited = 0, deadline = Date.now() + 450;
                    var walker = document.createTreeWalker(root, NodeFilter.SHOW_ELEMENT | NodeFilter.SHOW_TEXT, {
                        acceptNode: function (node) {
                            if (node.nodeType === 3) return NodeFilter.FILTER_ACCEPT;
                            if (excluded.test(node.tagName) || node.hidden || node.isContentEditable ||
                                node.getAttribute("aria-hidden") === "true" || node.getAttribute("role") === "navigation")
                                return NodeFilter.FILTER_REJECT;
                            var style = window.getComputedStyle(node);
                            if (style.display === "none" || style.visibility === "hidden") return NodeFilter.FILTER_REJECT;
                            return NodeFilter.FILTER_ACCEPT;
                        }
                    });
                    var node;
                    while ((node = walker.nextNode())) {
                        if (++visited > 24000 || Date.now() > deadline || output.length >= limit) {
                            reply.truncated = true;
                            break;
                        }
                        if (node.nodeType === 3) {
                            var available = limit - output.length;
                            var raw = node.nodeValue;
                            if (raw.length > available + 1) {
                                output += raw.slice(0, available + 1).replace(/\s+/g, " ");
                                reply.truncated = true;
                                break;
                            }
                            output += raw.replace(/\s+/g, " ");
                        } else if (blocks.test(node.tagName) && output && !/\n$/.test(output)) {
                            output += "\n";
                        }
                    }
                    if (output.length > limit) reply.truncated = true;
                    reply.text = output.slice(0, limit).replace(/ *\n */g, "\n").replace(/\n{3,}/g, "\n\n").trim();
                }
            }
            // Avoid returning an unmatched surrogate at the UTF-16 boundary.
            reply.text = reply.text.replace(/[\uD800-\uDBFF]$/, "");
        } catch (error) {
            reply.text = "";
        } finally {
            arguments.completionFunction(reply);
        }
    }
};
