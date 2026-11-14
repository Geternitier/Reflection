const { mathjax } = require('mathjax-full/js/mathjax.js');
const { TeX } = require('mathjax-full/js/input/tex.js');
const { SVG } = require('mathjax-full/js/output/svg.js');
const { STATE } = require('mathjax-full/js/core/MathItem.js');
const { liteAdaptor } = require('mathjax-full/js/adaptors/liteAdaptor.js');
const { RegisterHTMLHandler } = require('mathjax-full/js/handlers/html.js');
const { Configuration } = require('mathjax-full/js/input/tex/Configuration.js');
const { CommandMap } = require('mathjax-full/js/input/tex/SymbolMap.js');
const TexParser = require('mathjax-full/js/input/tex/TexParser.js').default;
const PhysicsMethods = require('mathjax-full/js/input/tex/physics/PhysicsMethods.js').default;
require('mathjax-full/js/input/tex/ams/AmsConfiguration.js');
require('mathjax-full/js/input/tex/mathtools/MathtoolsConfiguration.js');
require('mathjax-full/js/input/tex/cancel/CancelConfiguration.js');
require('mathjax-full/js/input/tex/boldsymbol/BoldsymbolConfiguration.js');
require('mathjax-full/js/input/tex/mhchem/MhchemConfiguration.js');
require('mathjax-full/js/input/tex/physics/PhysicsConfiguration.js');
require('mathjax-full/js/input/tex/unicode/UnicodeConfiguration.js');

const units = {
    metre: 'm', meter: 'm', second: 's', gram: 'g', kilogram: 'kg',
    ampere: 'A', kelvin: 'K', mole: 'mol', candela: 'cd', hertz: 'Hz',
    newton: 'N', pascal: 'Pa', joule: 'J', watt: 'W', volt: 'V',
    coulomb: 'C', ohm: '\\Omega', litre: 'L', liter: 'L', minute: 'min',
    hour: 'h', degree: '^{\\circ}', degreeCelsius: '^{\\circ}C',
    percent: '\\%', micro: '\\mu', milli: 'm', centi: 'c', kilo: 'k',
    mega: 'M', giga: 'G', nano: 'n', per: '/', squared: '^{2}', cubed: '^{3}'
};

function quantity(parser, name, requireUnit) {
    const initial = parser.i;
    if (!requireUnit && parser.GetNext() !== '{') {
        parser.i = initial;
        return PhysicsMethods.Quantity(parser, name);
    }
    const number = parser.GetArgument(name);
    if (!requireUnit && parser.GetNext() !== '{') {
        parser.i = initial;
        return PhysicsMethods.Quantity(parser, name);
    }
    const unit = parser.GetArgument(name).replace(/\\([A-Za-z]+)\s*/g, (_, command) => {
        if (!Object.hasOwn(units, command)) throw new Error('Unsupported unit command');
        return units[command];
    });
    if (!/^[+\-]?(?:\d+(?:[.,]\d*)?|[.,]\d+)(?:[eE][+\-]?\d+)?$/.test(number.trim()) ||
        !/^(?:[a-zA-Z0-9 {}^./%\-]|\\(?:Omega|mu|circ|%))+$/.test(unit))
        throw new Error('Unsupported quantity');
    const value = number.trim().replace(/[eE]([+\-]?\d+)$/, (_, exponent) => '\\times 10^{' + exponent + '}');
    const notation = unit.replace(/\s+/g, '\\,');
    parser.Push(new TexParser(value + '\\,\\mathrm{' + notation + '}', parser.stack.env, parser.configuration).mml());
}

new CommandMap('reflection-math', {
    SI: ['Quantity', true],
    qty: ['Quantity', false],
    vcentcolon: ['CenteredColon']
}, {
    Quantity: quantity,
    CenteredColon: parser => parser.Push(new TexParser('\\mathrel{\\vcenter{\\hbox{:}}}', parser.stack.env,
        parser.configuration).mml())
});
Configuration.create('reflection-math', { handler: { macro: ['reflection-math'] }, priority: 0 });

const adaptor = liteAdaptor();
RegisterHTMLHandler(adaptor);
const tex = new TeX({
    packages: ['base', 'ams', 'mathtools', 'cancel', 'boldsymbol', 'mhchem', 'physics', 'unicode', 'reflection-math'],
    tags: 'ams', maxBuffer: 8192, maxMacros: 256,
    formatError: (_, error) => { throw error; }
});
const document = mathjax.document('', { InputJax: tex, OutputJax: new SVG({ fontCache: 'none', internalSpeechTitles: false }) });
const pixels = value => Number.parseFloat(value) * 7.072;
const svgFor = (source, display) => adaptor.firstChild(document.convert(source, { display, em: 16, ex: 7.072 }));

function resetFormula() {
    tex.parseOptions.clear();
    tex.reset();
    const tags = tex.parseOptions.tags;
    tags.labels = {};
    tags.ids = {};
    tags.stack = [];
}

globalThis.indexFormula = (source, display, firstNumber) => {
    tex.reset(firstNumber);
    try {
        document.convert(source, { display, end: STATE.COMPILED });
        const tags = tex.parseOptions.tags;
        return JSON.stringify({ Counter: tags.allCounter, Labels: tags.allLabels });
    } finally {
        resetFormula();
    }
};

// A numbered table uses full-viewport nested SVGs for its cells and labels.
// Resolve their viewport alignment before handing the image to Skia.
function resolveViewports(svg, width, height) {
    for (const nested of adaptor.tags(svg, 'svg')) {
        if (nested === svg) continue;
        const [x, y, w, h] = adaptor.getAttribute(nested, 'viewBox').split(/\s+/).map(Number);
        const align = adaptor.getAttribute(nested, 'preserveAspectRatio');
        const scale = Math.min(width / w, height / h);
        const extra = width - w * scale;
        const dx = align.startsWith('xMax') ? extra : align.startsWith('xMid') ? extra / 2 : 0;
        const dy = (height - h * scale) / 2;
        const transform = `translate(${dx - x * scale},${dy - y * scale}) scale(${scale})`;
        adaptor.replace(adaptor.node('g', { transform }, adaptor.childNodes(nested)), nested);
    }
}

globalThis.renderFormula = (source, display, firstNumber, labels, color) => {
    tex.reset(firstNumber);
    try {
        Object.assign(tex.parseOptions.tags.allLabels, JSON.parse(labels));
        const svg = svgFor(source, display);
        const widthAttribute = adaptor.getAttribute(svg, 'width');
        const fullWidth = widthAttribute === '100%';
        let width = pixels(fullWidth ? adaptor.getStyle(svg, 'min-width') : widthAttribute);
        let height = pixels(adaptor.getAttribute(svg, 'height'));
        if (fullWidth) {
            resolveViewports(svg, width, height);
            adaptor.setAttribute(svg, 'viewBox', `0 0 ${width} ${height}`);
        }
        // These commands omit parts of their ink from the surrounding formula's metrics.
        // Reserve the natural extent at the bitmap boundary while preserving the layout inside.
        const unpadded = source.replace(/\\(?:smash(?![a-zA-Z])(?:\s*\[[tb]*\])?|mathclap(?![a-zA-Z]))/g, '');
        if (unpadded !== source) {
            resetFormula();
            tex.reset(firstNumber);
            Object.assign(tex.parseOptions.tags.allLabels, JSON.parse(labels));
            const natural = svgFor(unpadded, display);
            const depth = -pixels(adaptor.getStyle(svg, 'vertical-align'));
            const naturalDepth = -pixels(adaptor.getStyle(natural, 'vertical-align'));
            const above = Math.max(0, pixels(adaptor.getAttribute(natural, 'height')) - naturalDepth - (height - depth));
            const below = Math.max(0, naturalDepth - depth);
            const [x, y, w, h] = adaptor.getAttribute(svg, 'viewBox').split(/\s+/).map(Number);
            const units = h / height;
            const naturalWidth = adaptor.getAttribute(natural, 'width');
            const sides = Math.max(0, pixels(naturalWidth === '100%' ?
                adaptor.getStyle(natural, 'min-width') : naturalWidth) - width);
            adaptor.setAttribute(svg, 'viewBox',
                `${x - sides * units / 2} ${y - above * units} ${w + sides * units} ${h + (above + below) * units}`);
            width += sides;
            height += above + below;
        }
        adaptor.setAttribute(svg, 'width', width);
        adaptor.setAttribute(svg, 'height', height);
        const group = adaptor.firstChild(svg);
        adaptor.setAttribute(group, 'fill', color);
        adaptor.setAttribute(group, 'stroke', color);
        return adaptor.outerHTML(svg);
    } finally {
        resetFormula();
    }
};
