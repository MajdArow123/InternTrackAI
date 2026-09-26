#!/usr/bin/env python3
"""
InternTrackAI — Overview document.
Apple-style editorial layout, vector diagrams, three-colour palette.
"""
import os
from reportlab.pdfgen import canvas
from reportlab.lib.pagesizes import letter
from reportlab.lib.colors import HexColor, Color
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont

# ---------------------------------------------------------------- fonts
FDIR = "/home/claude/fonts/inter_x/extras/ttf"
pdfmetrics.registerFont(TTFont("Inter", f"{FDIR}/Inter-Regular.ttf"))
pdfmetrics.registerFont(TTFont("Inter-Md", f"{FDIR}/Inter-Medium.ttf"))
pdfmetrics.registerFont(TTFont("Inter-Sb", f"{FDIR}/Inter-SemiBold.ttf"))
pdfmetrics.registerFont(TTFont("Inter-Bd", f"{FDIR}/Inter-Bold.ttf"))
pdfmetrics.registerFont(TTFont("Inter-Lt", f"{FDIR}/Inter-Light.ttf"))

REG, MED, SEM, BLD, LGT = "Inter", "Inter-Md", "Inter-Sb", "Inter-Bd", "Inter-Lt"

# ---------------------------------------------------------------- palette
PLUM   = HexColor("#4D444F")   # PANTONE P 101-16 U
BLUE   = HexColor("#B9CADF")   # PANTONE P 109-10 U
CREAM  = HexColor("#F2E5C8")   # PANTONE P 7-9 U
WHITE  = HexColor("#FFFFFF")
HAIR   = HexColor("#E2DFE3")

def tint(c, pct):
    """Lighter tint of a colour toward white. pct 0..1"""
    return Color(c.red + (1 - c.red) * pct,
                 c.green + (1 - c.green) * pct,
                 c.blue + (1 - c.blue) * pct)

def shade(c, pct):
    """Darker shade toward black."""
    return Color(c.red * (1 - pct), c.green * (1 - pct), c.blue * (1 - pct))

PLUM_60  = tint(PLUM, 0.40)    # secondary text
PLUM_40  = tint(PLUM, 0.60)    # tertiary text
BLUE_30  = tint(BLUE, 0.62)    # soft fills
BLUE_55  = tint(BLUE, 0.42)
CREAM_50 = tint(CREAM, 0.50)
PLUM_DK  = shade(PLUM, 0.25)

# ---------------------------------------------------------------- page
PW, PH = letter                      # 612 x 792
ML, MR = 58, 58
MT, MB = 62, 56
CW = PW - ML - MR                    # 496 content width

DOC_NAME = "InternTrackAI  ·  Technical Overview"

class Doc:
    def __init__(self, path):
        self.c = canvas.Canvas(path, pagesize=letter)
        self.c.setTitle("InternTrackAI — Technical Overview")
        self.c.setAuthor("Mjd Arow")
        self.c.setSubject("Product and architecture overview")
        self.page = 0
        self.toc = []

    def newpage(self, chrome=True):
        if self.page > 0:
            self.c.showPage()
        self.page += 1
        if chrome:
            self._chrome()
        return PH - MT

    def _chrome(self):
        c = self.c
        # top accent rule
        c.setStrokeColor(BLUE); c.setLineWidth(2.2)
        c.line(ML, PH - 40, ML + 34, PH - 40)
        # footer
        c.setFont(REG, 7.2); c.setFillColor(PLUM_40)
        c.drawString(ML, 34, DOC_NAME)
        c.drawRightString(PW - MR, 34, str(self.page))
        c.setStrokeColor(HAIR); c.setLineWidth(0.5)
        c.line(ML, 46, PW - MR, 46)

    def save(self):
        self.c.showPage()
        self.c.save()

# ---------------------------------------------------------------- text
def wrap(text, font, size, maxw):
    """Greedy wrap -> list of lines that fit maxw."""
    words, lines, cur = text.split(), [], ""
    for w in words:
        t = (cur + " " + w).strip()
        if pdfmetrics.stringWidth(t, font, size) <= maxw:
            cur = t
        else:
            if cur:
                lines.append(cur)
            cur = w
    if cur:
        lines.append(cur)
    return lines

def para(c, text, x, y, w, font=REG, size=9.6, lead=15.2, color=PLUM,
         align="left"):
    c.setFont(font, size); c.setFillColor(color)
    for ln in wrap(text, font, size, w):
        if align == "center":
            c.drawCentredString(x + w / 2, y, ln)
        else:
            c.drawString(x, y, ln)
        y -= lead
    return y

def eyebrow(c, text, x, y, color=PLUM_40):
    c.setFont(SEM, 7.4); c.setFillColor(color)
    c.drawString(x, y, " ".join(text.upper()))
    return y - 17

def title(c, text, x, y, size=27, color=PLUM, w=CW):
    c.setFont(BLD, size); c.setFillColor(color)
    lead = size * 1.18
    for ln in wrap(text, BLD, size, w):
        c.drawString(x, y, ln); y -= lead
    return y - 4

def lede(c, text, x, y, w=CW, size=11.4, color=PLUM_60):
    return para(c, text, x, y, w, font=LGT, size=size, lead=17.4, color=color)

def bullet(c, text, x, y, w, font=REG, size=9.6, lead=14.6, color=PLUM,
           dot=BLUE):
    c.setFillColor(dot)
    c.circle(x + 2.4, y + 3.2, 1.9, fill=1, stroke=0)
    return para(c, text, x + 12, y, w - 12, font, size, lead, color)

def rule(c, x, y, w, color=HAIR, lw=0.6):
    c.setStrokeColor(color); c.setLineWidth(lw)
    c.line(x, y, x + w, y)

# ---------------------------------------------------------------- shapes
def rbox(c, x, y, w, h, fill=None, stroke=HAIR, r=10, lw=0.8, dash=None):
    if fill is not None:
        c.setFillColor(fill)
    if stroke is not None:
        c.setStrokeColor(stroke); c.setLineWidth(lw)
        if dash:
            c.setDash(dash)
    c.roundRect(x, y, w, h, r, fill=1 if fill is not None else 0,
                stroke=1 if stroke is not None else 0)
    c.setDash()

def boxtext(c, x, y, w, h, text, sub=None, fill=BLUE_30, stroke=None,
            tc=PLUM, font=SEM, size=8.6, subsize=7.3, r=9, num=None):
    """Rounded box with vertically-centred wrapped label (+ optional subtitle)."""
    rbox(c, x, y, w, h, fill=fill, stroke=stroke, r=r)
    pad = 9
    lines = wrap(text, font, size, w - pad * 2)
    sublines = wrap(sub, REG, subsize, w - pad * 2) if sub else []
    lead, sublead = size * 1.28, subsize * 1.30
    total = len(lines) * lead + (len(sublines) * sublead + 3 if sublines else 0)
    ty = y + h / 2 + total / 2 - lead + 3.2
    c.setFont(font, size); c.setFillColor(tc)
    for ln in lines:
        c.drawCentredString(x + w / 2, ty, ln); ty -= lead
    if sublines:
        ty -= 1
        c.setFont(REG, subsize); c.setFillColor(tint(tc, 0.35))
        for ln in sublines:
            c.drawCentredString(x + w / 2, ty, ln); ty -= sublead
    if num is not None:
        c.setFillColor(PLUM); c.circle(x + 11, y + h - 11, 7.4, fill=1, stroke=0)
        c.setFont(SEM, 7.2); c.setFillColor(WHITE)
        c.drawCentredString(x + 11, y + h - 13.4, str(num))

def arrow(c, x1, y1, x2, y2, color=PLUM_60, lw=1.0, head=4.6, dash=None):
    c.setStrokeColor(color); c.setLineWidth(lw)
    if dash:
        c.setDash(dash)
    c.line(x1, y1, x2, y2)
    c.setDash()
    import math
    ang = math.atan2(y2 - y1, x2 - x1)
    c.setFillColor(color)
    p = c.beginPath()
    p.moveTo(x2, y2)
    p.lineTo(x2 - head * math.cos(ang - 0.42), y2 - head * math.sin(ang - 0.42))
    p.lineTo(x2 - head * math.cos(ang + 0.42), y2 - head * math.sin(ang + 0.42))
    p.close()
    c.drawPath(p, fill=1, stroke=0)

def alabel(c, text, x, y, size=6.9, color=PLUM_40, align="center", bg=WHITE):
    c.setFont(REG, size)
    w = pdfmetrics.stringWidth(text, REG, size)
    if bg is not None:
        c.setFillColor(bg)
        if align == "center":
            c.rect(x - w / 2 - 3, y - 2.2, w + 6, size + 2, fill=1, stroke=0)
        else:
            c.rect(x - 3, y - 2.2, w + 6, size + 2, fill=1, stroke=0)
    c.setFillColor(color)
    if align == "center":
        c.drawCentredString(x, y, text)
    else:
        c.drawString(x, y, text)

def legend(c, items, x, y, size=7.2):
    """items = [(color, label), ...] laid out horizontally."""
    cx = x
    for col, lab in items:
        rbox(c, cx, y, 11, 8, fill=col, stroke=None, r=2.5)
        c.setFont(REG, size); c.setFillColor(PLUM_60)
        c.drawString(cx + 15, y + 1.2, lab)
        cx += 15 + pdfmetrics.stringWidth(lab, REG, size) + 22
    return y - 14

# ---------------------------------------------------------------- build
OUT = "/home/claude/out/InternTrackAI-Overview.pdf"
os.makedirs("/home/claude/out", exist_ok=True)
d = Doc(OUT)
c = d.c

# ================================================================ 1 COVER
d.newpage(chrome=False)
c.setFillColor(CREAM); c.rect(0, 0, PW, PH, fill=1, stroke=0)
# quiet geometric mark
c.setFillColor(BLUE)
c.roundRect(ML, PH - 210, 58, 58, 14, fill=1, stroke=0)
c.setFillColor(PLUM)
c.roundRect(ML + 20, PH - 190, 22, 22, 6, fill=1, stroke=0)

c.setFont(SEM, 8.2); c.setFillColor(PLUM_60)
c.drawString(ML, PH - 252, " ".join("PRODUCT & ARCHITECTURE OVERVIEW"))

c.setFont(BLD, 52); c.setFillColor(PLUM)
c.drawString(ML, PH - 316, "InternTrackAI")

y = PH - 352
y = para(c, "An AI-assisted internship application tracker that tells you "
            "which roles you actually match — before you apply.",
         ML, y, CW - 90, font=LGT, size=15.2, lead=23, color=PLUM_60)

iy = PH - 452
c.setFont(SEM, 7.4); c.setFillColor(PLUM_60)
c.drawString(ML, iy, " ".join("INSIDE"))
iy -= 20
inside = [("Feature tour", "Twelve shipped capabilities"),
          ("Six diagrams", "Journey, architecture, lifecycle, data, AI, deploy"),
          ("Engineering notes", "Three findings, each overturned by measurement"),
          ("Appendix", "Routes and configuration keys")]
for nm, ds in inside:
    c.setFillColor(PLUM); c.circle(ML + 2.6, iy + 3, 2.6, fill=1, stroke=0)
    c.setFont(SEM, 8.6); c.setFillColor(PLUM)
    c.drawString(ML + 13, iy, nm)
    c.setFont(REG, 8.6); c.setFillColor(PLUM_60)
    c.drawString(ML + 128, iy, ds)
    iy -= 17

rule(c, ML, 232, CW)
c.setFont(SEM, 8.6); c.setFillColor(PLUM)
c.drawString(ML, 210, "Mjd Arow")
c.setFont(REG, 8.6); c.setFillColor(PLUM_60)
c.drawString(ML, 194, "Computer Programming & Analysis, George Brown College")

c.setFont(SEM, 8.6); c.setFillColor(PLUM)
c.drawRightString(PW - MR, 210, "interntrackai.majdarow.com")
c.setFont(REG, 8.6); c.setFillColor(PLUM_60)
c.drawRightString(PW - MR, 194, "September 2026")

c.setFillColor(PLUM)
c.roundRect(ML, 96, 306, 62, 12, fill=1, stroke=0)
c.setFont(REG, 8.4); c.setFillColor(tint(CREAM, 0.15))
c.drawString(ML + 18, 132, "Live, deployed, and in daily use.")
c.setFont(REG, 7.8); c.setFillColor(tint(BLUE, 0.28))
c.drawString(ML + 18, 116, "ASP.NET Core 9  ·  EF Core 9  ·  PostgreSQL  ·  GPT-4o-mini")

# ================================================================ 2 CONTENTS
y = d.newpage()
y = eyebrow(c, "Contents", ML, y)
y = title(c, "What's in this document", ML, y - 6, size=26)
y -= 16

sections = [
    ("01", "At a glance", "What it is, who it's for, and the numbers", 3),
    ("02", "The idea", "The problem this was built to solve", 4),
    ("03", "Feature tour", "Every shipped capability, one card each", 5),
    ("04", "User journey", "Sign up through to interview prep", 8),
    ("05", "System architecture", "Browser, MVC, services, data, OpenAI", 9),
    ("06", "Request lifecycle", "One flow, step by step", 10),
    ("07", "Data model", "Entities and relationships", 11),
    ("08", "AI pipeline", "How every AI feature is built and guarded", 12),
    ("09", "Field awareness", "Why this isn't a software-only tool", 13),
    ("10", "The practice engine", "Generation, de-duplication, scoring", 14),
    ("11", "Security & data", "Auth, scoping, secrets, deletion", 16),
    ("12", "Deployment", "Local to production", 17),
    ("13", "Testing", "What's covered, and what isn't", 18),
    ("14", "Engineering notes", "Three findings worth reading", 19),
    ("15", "Tech stack", "Every choice, and why", 21),
    ("16", "Roadmap", "Planned, not built", 22),
    ("17", "Appendix", "Routes and configuration keys", 23),
]
for num, name, desc, pg in sections:
    c.setFont(SEM, 8.2); c.setFillColor(BLUE)
    c.drawString(ML, y, num)
    c.setFont(SEM, 10.4); c.setFillColor(PLUM)
    c.drawString(ML + 30, y, name)
    c.setFont(REG, 8.6); c.setFillColor(PLUM_40)
    c.drawString(ML + 176, y, desc)
    c.setFont(REG, 8.6); c.setFillColor(PLUM_60)
    c.drawRightString(PW - MR, y, str(pg))
    y -= 11
    rule(c, ML, y, CW)
    y -= 20

# ================================================================ 3 AT A GLANCE
y = d.newpage()
y = eyebrow(c, "01  At a glance", ML, y)
y = title(c, "One place for every application, deadline and interview", ML, y - 6)
y = lede(c, "InternTrackAI is a full-stack web application that tracks internship "
            "and new-grad applications, and uses a language model to do the work "
            "around them: reading job descriptions, scoring a resume against a "
            "posting, drafting cover letters, and generating interview questions "
            "you can answer and be graded on.", ML, y - 10)
y -= 22

stats = [("12", "AI-backed services"), ("1,392", "automated tests"),
         ("13", "field categories"), ("1", "production region")]
bw = (CW - 3 * 12) / 4
for i, (big, lab) in enumerate(stats):
    bx = ML + i * (bw + 12)
    rbox(c, bx, y - 62, bw, 62, fill=CREAM_50, stroke=None, r=11)
    c.setFont(BLD, 23); c.setFillColor(PLUM)
    c.drawString(bx + 14, y - 33, big)
    c.setFont(REG, 7.6); c.setFillColor(PLUM_60)
    for j, ln in enumerate(wrap(lab, REG, 7.6, bw - 26)):
        c.drawString(bx + 14, y - 47 - j * 9.4, ln)
y -= 86

col = (CW - 22) / 2
ly = y
c.setFont(SEM, 9.6); c.setFillColor(PLUM)
c.drawString(ML, ly, "Who it's for")
ly -= 16
ly = para(c, "Students and new graduates running twenty or more applications at "
             "once, who lose track of which resume went where, which deadline is "
             "next, and which posting they were actually a good fit for.",
          ML, ly, col, size=9.2, lead=14.4, color=PLUM_60)

ry = y
c.setFont(SEM, 9.6); c.setFillColor(PLUM)
c.drawString(ML + col + 22, ry, "What makes it different")
ry -= 16
ry = para(c, "It is not a software-only tool. The app detects the user's field "
             "from their resume and adapts every AI prompt to it, so a nursing "
             "or accounting student gets questions and feedback from their own "
             "discipline rather than a developer's.",
          ML + col + 22, ry, col, size=9.2, lead=14.4, color=PLUM_60)

y = min(ly, ry) - 26
rbox(c, ML, y - 78, CW, 78, fill=BLUE_30, stroke=None, r=12)
c.setFont(SEM, 9.2); c.setFillColor(PLUM)
c.drawString(ML + 18, y - 24, "Three headline facts")
fy = y - 42
facts = ["Deployed to a custom domain and reachable publicly, with a shared "
         "demo account that reseeds nightly.",
         "Every AI call is rate-limited per user across two separate budgets, "
         "and capped by a wall-clock timeout.",
         "Schema correctness is verified against a real PostgreSQL instance, "
         "not only the local SQLite database."]
for f in facts:
    fy = bullet(c, f, ML + 18, fy, CW - 36, size=8.4, lead=11.6,
                color=PLUM, dot=PLUM) - 1.5

y = y - 112
c.setFont(SEM, 9.6); c.setFillColor(PLUM)
c.drawString(ML, y, "How the pieces connect")
y -= 14
para(c, "Each stage produces the input for the next. That chain is what separates "
        "this from a spreadsheet with an AI button attached.",
     ML, y, CW, size=8.8, lead=12.6, color=PLUM_60)
y -= 34
chain = [("Resume", "parsed and confirmed"),
         ("Profile", "field, skills, roles"),
         ("Posting", "analysed into skills"),
         ("Match", "overlap and gaps"),
         ("Prepare", "questions and scores")]
cbw = (CW - 4 * 16) / 5
for i, (t, s2) in enumerate(chain):
    x = ML + i * (cbw + 16)
    fill = CREAM_50 if i % 2 == 0 else BLUE_30
    boxtext(c, x, y - 52, cbw, 52, t, sub=s2, fill=fill, stroke=None,
            size=8.8, subsize=6.4, r=9)
    if i < 4:
        arrow(c, x + cbw + 2.5, y - 26, x + cbw + 13.5, y - 26, head=4.0)

# ================================================================ 4 THE IDEA
y = d.newpage()
y = eyebrow(c, "02  The idea", ML, y)
y = title(c, "The admin around applying is the part nobody builds for", ML, y - 6)
y = lede(c, "Job boards help you find postings. Spreadsheets help you remember "
            "them. Neither helps with the thinking in between.", ML, y - 10)
y -= 26

probs = [
    ("The tracking problem",
     "Twenty applications across five job boards, three resume versions, and a "
     "deadline you only remember because it already passed. A spreadsheet holds "
     "the data but answers no questions."),
    ("The fit problem",
     "Every posting looks plausible at 11pm. Knowing whether your resume "
     "actually covers what a posting asks for requires reading both carefully, "
     "side by side, every time."),
    ("The preparation problem",
     "Interview prep is generic until it isn't. Practising \"tell me about "
     "yourself\" is not the same as practising for the specific backend role "
     "you have an interview for on Thursday."),
]
for h, b in probs:
    rbox(c, ML, y - 74, CW, 74, fill=WHITE, stroke=HAIR, r=12)
    c.setFillColor(BLUE); c.roundRect(ML, y - 74, 3.5, 74, 1.6, fill=1, stroke=0)
    c.setFont(SEM, 10); c.setFillColor(PLUM)
    c.drawString(ML + 20, y - 24, h)
    para(c, b, ML + 20, y - 41, CW - 42, size=8.8, lead=13, color=PLUM_60)
    y -= 88

y -= 6
c.setFillColor(PLUM); c.roundRect(ML, y - 118, CW, 118, 12, fill=1, stroke=0)
c.setFont(SEM, 8); c.setFillColor(BLUE)
c.drawString(ML + 22, y - 28, " ".join("THE PREMISE"))
para(c, "Track, analyse, prepare — in one place, where each step feeds the next. "
        "The application you saved becomes the posting the analyser reads, which "
        "becomes the resume score, which becomes the cover letter, which becomes "
        "the interview questions you practise the night before.",
     ML + 22, y - 52, CW - 44, font=LGT, size=11.2, lead=16.6,
     color=tint(CREAM, 0.1))

# ================================================================ 5-7 FEATURES
FEATURES = [
    ("Application tracking",
     "A list view and a drag-and-drop board across five stages: Saved, Applied, "
     "Interview, Offer, Rejected. Deadlines, work mode, notes and per-application "
     "history.",
     "Every other feature hangs off an application record."),
    ("Dashboard",
     "Counts by stage, applications over time, response rate by resume version, "
     "an attention card for what needs doing next, and a practice summary.",
     "Answers \"what should I do today\" without opening anything else."),
    ("Job description analyzer",
     "Paste a posting. The model returns the key skills, requirements and "
     "responsibilities as structured data, stored against the application.",
     "Turns a wall of text into something the rest of the app can use."),
    ("Resume match scoring",
     "Scores an uploaded resume against a specific posting and reports the "
     "overlap and the gaps as a percentage plus a written explanation.",
     "Tells you whether a posting is worth the hour it takes to apply."),
    ("Resume upload and review",
     "PDF or Word. Text is extracted server-side, parsed by the model into field, "
     "skills with supporting evidence, seniority and target roles — then shown on "
     "a review screen where you confirm before anything is written.",
     "No AI output reaches the profile without the user seeing it first."),
    ("Cover letter generator",
     "Drafts a letter from the profile and the posting, with a rewrite pass for "
     "tightening specific paragraphs.",
     "The first draft is the hard part; this removes it."),
    ("Interview prep",
     "Generates questions grounded in a specific posting, split across technical, "
     "behavioural and company-specific categories.",
     "Prep for the interview you actually have, not interviews in general."),
    ("Practice engine",
     "Questions at three difficulties across three categories, with de-duplication, "
     "written answers scored one to five with two concrete improvements, retry "
     "with side-by-side attempt comparison, and progress tracking.",
     "The part that turns a tracker into a preparation tool."),
    ("Inbox suggestions",
     "Optional read-only Gmail connection. Matched messages are classified and "
     "surfaced as suggested status changes for you to accept or dismiss.",
     "Rejection emails update the board without you touching it."),
    ("Salary insight",
     "An estimate for a role and location, scoped to the seniority on the profile.",
     "Context before a conversation about numbers."),
    ("Follow-up drafting",
     "Drafts a follow-up message for an application that has gone quiet.",
     "The message you keep meaning to send."),
    ("Guided tour",
     "A five-step walkthrough in the order a user actually works: set up, track, "
     "prepare. Never auto-plays; offered, not imposed.",
     "A first-time visitor sees the point in sixty seconds."),
]

for page_i in range(3):
    y = d.newpage()
    if page_i == 0:
        y = eyebrow(c, "03  Feature tour", ML, y)
        y = title(c, "Everything that ships today", ML, y - 6)
        y = lede(c, "Twelve capabilities, all live. Nothing on these three pages "
                    "is planned or partial.", ML, y - 10)
        y -= 20
    else:
        y = eyebrow(c, f"03  Feature tour  ·  continued", ML, y)
        y -= 6

    chunk = FEATURES[page_i * 4:(page_i + 1) * 4]
    for name, what, why in chunk:
        h = 118 if page_i == 0 else 132
        rbox(c, ML, y - h, CW, h, fill=WHITE, stroke=HAIR, r=12)
        c.setFont(SEM, 11.4); c.setFillColor(PLUM)
        c.drawString(ML + 20, y - 27, name)
        ty = para(c, what, ML + 20, y - 47, CW - 42, size=9, lead=13.4,
                  color=PLUM_60)
        rule(c, ML + 20, ty - 2, CW - 42)
        c.setFont(SEM, 7.2); c.setFillColor(BLUE)
        c.drawString(ML + 20, ty - 18, " ".join("WHY IT MATTERS"))
        para(c, why, ML + 20, ty - 32, CW - 42, size=8.6, lead=12.4, color=PLUM)
        y -= h + 14

# ================================================================ 8 JOURNEY
y = d.newpage()
y = eyebrow(c, "04  User journey", ML, y)
y = title(c, "From sign-up to the night before the interview", ML, y - 6)
y = lede(c, "Each step produces something the next step consumes. That chain is "
            "the product.", ML, y - 10)
y -= 30

steps = [
    ("Sign up", "Email and password, or the shared demo"),
    ("Upload resume", "Parsed, reviewed, confirmed into the profile"),
    ("Add application", "Company, role, deadline, mode"),
    ("Analyze posting", "Skills and requirements extracted"),
    ("Score the match", "Overlap and gaps against your resume"),
    ("Draft cover letter", "Generated from profile plus posting"),
    ("Track status", "Board, deadlines, inbox suggestions"),
    ("Prepare", "Questions from the posting, answered and scored"),
]
bw2, bh2, gap = 108, 56, 18
for i, (t, s) in enumerate(steps):
    col_i, row_i = i % 4, i // 4
    x = ML + col_i * (bw2 + gap)
    yy = y - row_i * (bh2 + 56)
    fill = CREAM_50 if row_i == 0 else BLUE_30
    boxtext(c, x, yy - bh2, bw2, bh2, t, sub=s, fill=fill, stroke=None,
            size=8.8, subsize=6.8, num=i + 1)
    if col_i < 3:
        arrow(c, x + bw2 + 3, yy - bh2 / 2, x + bw2 + gap - 3, yy - bh2 / 2)

# wrap arrow row1 -> row2
x_end = ML + 3 * (bw2 + gap) + bw2
y_mid1 = y - bh2 / 2
y_mid2 = y - (bh2 + 56) - bh2 / 2
c.setStrokeColor(PLUM_60); c.setLineWidth(1.0)
c.line(x_end + 3, y_mid1, x_end + 14, y_mid1)
c.line(x_end + 14, y_mid1, x_end + 14, y_mid1 - 28)
c.line(x_end + 14, y_mid1 - 28, ML - 14, y_mid1 - 28)
c.line(ML - 14, y_mid1 - 28, ML - 14, y_mid2)
arrow(c, ML - 14, y_mid2, ML - 3, y_mid2)

y = y - (bh2 + 56) - bh2 - 34
rbox(c, ML, y - 66, CW, 66, fill=WHITE, stroke=HAIR, r=12)
c.setFont(SEM, 9); c.setFillColor(PLUM)
c.drawString(ML + 18, y - 22, "The loop closes")
para(c, "Practice questions generated from an application stay grouped under that "
        "application. Answer one and the score appears on both the interview-prep "
        "page and the practice page — one question store, three entry points.",
     ML + 18, y - 38, CW - 36, size=8.6, lead=12.4, color=PLUM_60)

# ================================================================ 9 ARCHITECTURE
y = d.newpage()
y = eyebrow(c, "05  System architecture", ML, y)
y = title(c, "Server-rendered, single deployable, no SPA", ML, y - 6)
y = lede(c, "Razor views rendered on the server; dynamic behaviour is vanilla "
            "JavaScript fetching rendered partials. One process, one database, "
            "one external dependency.", ML, y - 10)
y -= 26

y = legend(c, [(BLUE_30, "In-process"), (CREAM_50, "Data"),
               (tint(PLUM, 0.78), "External")], ML, y) - 14

# Layer 1 browser
boxtext(c, ML, y - 46, CW, 46, "Browser",
        sub="Razor HTML  ·  vanilla JS  ·  fetch() returning rendered partials",
        fill=BLUE_30, stroke=None, size=10)
arrow(c, PW / 2, y - 50, PW / 2, y - 70)
alabel(c, "HTTPS", PW / 2 + 34, y - 62)
y -= 78

# Layer 2 middleware
boxtext(c, ML, y - 40, CW, 40, "ASP.NET Core 9  ·  middleware pipeline",
        sub="Identity authentication  ·  antiforgery  ·  rate limiting  ·  security headers",
        fill=tint(BLUE, 0.50), stroke=None, size=9.4)
arrow(c, PW / 2, y - 44, PW / 2, y - 62)
y -= 70

# Layer 3 controllers
cw3 = (CW - 3 * 9) / 4
ctrls = ["Applications", "Profile", "Practice", "Analyzer, Letters, Prep"]
for i, t in enumerate(ctrls):
    boxtext(c, ML + i * (cw3 + 9), y - 40, cw3, 40, t, fill=BLUE_30,
            stroke=None, size=8, r=8)
alabel(c, "MVC controllers", ML, y - 52, align="left")
y -= 62

# Layer 4 services
SVW = CW * 0.575
OAX = ML + CW * 0.665
boxtext(c, ML, y - 64, SVW, 64, "Domain & AI services",
        sub="12 model-backed services  ·  UserContextBuilder  ·  TopicKey  ·  "
            "PracticeProgress  ·  AiUsageLimiter",
        fill=tint(BLUE, 0.35), stroke=None, size=9.4)
boxtext(c, OAX, y - 64, PW - MR - OAX, 64, "OpenAI API",
        sub="gpt-4o-mini  ·  JSON schema mode  ·  30s timeout",
        fill=tint(PLUM, 0.78), stroke=None, size=9.4)
arrow(c, ML + SVW + 3, y - 24, OAX - 3, y - 24, head=3.8)
arrow(c, OAX - 3, y - 44, ML + SVW + 3, y - 44, head=3.8)
alabel(c, "prompt", (ML + SVW + OAX) / 2, y - 18, size=6.0, bg=None)
alabel(c, "JSON", (ML + SVW + OAX) / 2, y - 55, size=6.0, bg=None)
arrow(c, ML + CW * 0.28, y - 68, ML + CW * 0.28, y - 86)
y -= 94

# Layer 5 EF
boxtext(c, ML, y - 40, SVW, 40, "EF Core 9",
        sub="DbContext  ·  migrations  ·  per-user query scoping",
        fill=tint(BLUE, 0.50), stroke=None, size=9.4)
arrow(c, ML + CW * 0.28, y - 44, ML + CW * 0.28, y - 62)
y -= 70

# Layer 6 databases
boxtext(c, ML, y - 46, CW * 0.30, 46, "SQLite",
        sub="local development", fill=CREAM_50, stroke=None, size=9)
boxtext(c, ML + CW * 0.325, y - 46, CW * 0.29, 46, "PostgreSQL 18",
        sub="Railway, EU West", fill=CREAM_50, stroke=None, size=9)
boxtext(c, ML + CW * 0.645, y - 46, CW * 0.355, 46, "Volume",
        sub="uploaded resumes", fill=CREAM_50, stroke=None, size=9)

# ================================================================ 10 LIFECYCLE
y = d.newpage()
y = eyebrow(c, "06  Request lifecycle", ML, y)
y = title(c, "One flow: analysing a job description", ML, y - 6)
y = lede(c, "Ten steps from a paste into a textarea to structured skills stored "
            "against an application.", ML, y - 10)
y -= 30

lanes = ["Browser", "Controller", "Service", "OpenAI", "Database"]
lx = [ML + 30, ML + 140, ML + 246, ML + 352, ML + 452]
top = y
bottom = 152
for i, lab in enumerate(lanes):
    fill = tint(PLUM, 0.78) if lab == "OpenAI" else (
        CREAM_50 if lab == "Database" else BLUE_30)
    rbox(c, lx[i] - 44, top - 22, 88, 22, fill=fill, stroke=None, r=7)
    c.setFont(SEM, 7.6); c.setFillColor(PLUM)
    c.drawCentredString(lx[i], top - 15, lab)
    c.setStrokeColor(HAIR); c.setLineWidth(0.9); c.setDash(2, 3)
    c.line(lx[i], top - 26, lx[i], top - 46 - 9 * 41 - 14)
    c.setDash()

seq = [
    (0, 1, "POST /Analyzer/Analyze", "posting text + antiforgery token"),
    (1, 1, "Authorize + rate-limit check", "practice vs general budget"),
    (1, 2, "JobAnalyzerService.AnalyzeAsync", "user id, posting text"),
    (2, 2, "UserContextBuilder.BuildAsync", "field, seniority, skills"),
    (2, 3, "POST /v1/chat/completions", "prompt + strict JSON schema"),
    (3, 2, "Structured JSON", "skills, requirements, responsibilities"),
    (2, 2, "Parse, validate, clamp", "never throws; degrades to empty"),
    (2, 4, "Save analysis", "scoped to this user's application"),
    (4, 1, "Saved", ""),
    (1, 0, "Rendered partial", "swapped into the page, no reload"),
]
sy = top - 46
for i, (a, b, lab, sub) in enumerate(seq):
    c.setFillColor(PLUM); c.circle(ML + 6, sy + 2, 7.2, fill=1, stroke=0)
    c.setFont(SEM, 7); c.setFillColor(WHITE)
    c.drawCentredString(ML + 6, sy - 0.4, str(i + 1))
    if a == b:
        x0 = lx[a]
        c.setStrokeColor(PLUM_60); c.setLineWidth(1.0)
        c.line(x0, sy + 5, x0 + 22, sy + 5)
        c.line(x0 + 22, sy + 5, x0 + 22, sy - 5)
        arrow(c, x0 + 22, sy - 5, x0 + 1, sy - 5)
        c.setFont(MED, 7.2); c.setFillColor(PLUM)
        c.drawString(x0 + 30, sy + 1.4, lab)
        if sub:
            c.setFont(REG, 6.4); c.setFillColor(PLUM_40)
            c.drawString(x0 + 30, sy - 7.4, sub)
    else:
        d_ = 1 if b > a else -1
        arrow(c, lx[a] + d_ * 4, sy, lx[b] - d_ * 4, sy)
        midx = (lx[a] + lx[b]) / 2
        c.setFont(MED, 7.2)
        w = pdfmetrics.stringWidth(lab, MED, 7.2)
        c.setFillColor(WHITE); c.rect(midx - w / 2 - 3, sy + 3.4, w + 6, 9,
                                      fill=1, stroke=0)
        c.setFillColor(PLUM); c.drawCentredString(midx, sy + 5.4, lab)
        if sub:
            c.setFont(REG, 6.4); c.setFillColor(PLUM_40)
            c.drawCentredString(midx, sy - 9.6, sub)
    sy -= 41

rbox(c, ML, 62, CW, 42, fill=BLUE_30, stroke=None, r=10)
para(c, "Failure at step 5 or 6 never surfaces as an exception: the parser "
        "degrades to an empty result, the card shows an error, and the typed "
        "input is preserved.", ML + 16, 90, CW - 32, size=8.2, lead=12,
     color=PLUM)

# ================================================================ 11 DATA MODEL
y = d.newpage()
y = eyebrow(c, "07  Data model", ML, y)
y = title(c, "Everything hangs off the user", ML, y - 6)
y = lede(c, "Every domain table carries a user id, and every query filters on it. "
            "Ownership is enforced in the query, not in the view.", ML, y - 10)
y -= 26

def entity(c, x, y, w, name, fields, h=None, fill=WHITE, accent=BLUE):
    lh = 10.6
    h = h or (30 + len(fields) * lh + 8)
    rbox(c, x, y - h, w, h, fill=fill, stroke=HAIR, r=9)
    c.setFillColor(accent)
    c.roundRect(x, y - 24, w, 24, 9, fill=1, stroke=0)
    c.rect(x, y - 24, w, 12, fill=1, stroke=0)
    c.setFont(SEM, 8.2); c.setFillColor(PLUM)
    c.drawString(x + 10, y - 16, name)
    fy = y - 38
    for f in fields:
        c.setFont(REG, 6.9); c.setFillColor(PLUM_60)
        c.drawString(x + 10, fy, f)
        fy -= lh
    return h

ew = 148
# ApplicationUser
h1 = entity(c, ML + (CW - ew) / 2, y, ew, "ApplicationUser",
            ["Id  (PK)", "Email", "PasswordHash"], accent=tint(PLUM, 0.62))
c.setFont(REG, 6.6); c.setFillColor(PLUM_40)
c.drawCentredString(PW / 2, y - h1 - 12, "ASP.NET Core Identity")
y2 = y - h1 - 30

# fan-out arrows
cx = PW / 2
for dx in (-172, -58, 58, 172):
    c.setStrokeColor(PLUM_60); c.setLineWidth(0.9)
    c.line(cx, y2 + 16, cx, y2 + 8)
    c.line(cx, y2 + 8, cx + dx, y2 + 8)
    arrow(c, cx + dx, y2 + 8, cx + dx, y2 - 2)
alabel(c, "1 : many", cx + 96, y2 + 11, size=6.4)

y3 = y2 - 4
ew2 = 112
xs = [ML, ML + 128, ML + 256, ML + 384]
entity(c, xs[0], y3, ew2, "UserProfile",
       ["UserId  (FK)", "Field", "FieldCategory", "Seniority", "Location",
        "SkillsJson", "TargetRolesJson"])
entity(c, xs[1], y3, ew2, "JobApplication",
       ["Id  (PK)", "UserId  (FK)", "Company", "Role", "Status", "Deadline",
        "PostingText", "AnalysisJson"])
entity(c, xs[2], y3, ew2, "ResumeVersion",
       ["Id  (PK)", "UserId  (FK)", "Label", "FilePath", "ExtractedText",
        "IsActive"])
entity(c, xs[3], y3, ew2, "ParsedResume",
       ["Id  (PK)", "UserId  (FK)", "RawJson", "Applied", "CreatedAt"])

y4 = y3 - 146
# PracticeQuestion, wide
pqw = 300
pqx = ML + 56
hpq = entity(c, pqx, y4, pqw, "PracticeQuestion",
             ["Id  (PK)          UserId  (FK)          ApplicationId  (FK, nullable)",
              "Prompt          Topic          TopicKey basis          PromptHash",
              "Difficulty  (Easy / Medium / Hard)          Category          Source",
              "UserAnswer          AiFeedback          Score          AnsweredAt",
              "AnsweredInSeconds          PriorAttemptsJson          IsSaved"],
             accent=tint(BLUE, 0.15))
# link from JobApplication down to PracticeQuestion
c.setStrokeColor(PLUM_60); c.setLineWidth(0.9); c.setDash(2, 2.5)
c.line(xs[1] + ew2 / 2, y3 - 127, xs[1] + ew2 / 2, y4 + 12)
c.setDash()
arrow(c, xs[1] + ew2 / 2, y4 + 12, xs[1] + ew2 / 2, y4 - 2, dash=None)
alabel(c, "0..1 : many", xs[1] + ew2 / 2 + 60, y4 + 20, size=6.4)

y5 = y4 - hpq - 22
rbox(c, ML, y5 - 52, CW, 52, fill=CREAM_50, stroke=None, r=11)
c.setFont(SEM, 8.4); c.setFillColor(PLUM)
c.drawString(ML + 16, y5 - 20, "One unique index does the important work")
para(c, "PracticeQuestion carries a unique index on (UserId, PromptHash). Two "
        "users may be asked the same question; one user cannot be asked it twice.",
     ML + 16, y5 - 34, CW - 32, size=8.2, lead=11.6, color=PLUM_60)

y6 = y5 - 72
c.setFont(SEM, 9.4); c.setFillColor(PLUM)
c.drawString(ML, y6, "Three decisions worth naming")
y6 -= 20
dec = [("Enums stored as integers, with explicit values",
        "Matching the repository's existing convention, with explicit numeric "
        "values so appending or reordering a member can never shift stored rows."),
       ("Source recorded on every question",
        "A question generated from interview prep is distinguishable from one "
        "generated on the practice page, which is what lets topic comparison "
        "skip the broad ones."),
       ("Text columns declared explicitly",
        "Declared as text rather than inheriting a length limit, so an "
        "over-long AI summary cannot fail on PostgreSQL while succeeding "
        "locally.")]
for h2, b2 in dec:
    c.setFillColor(BLUE); c.circle(ML + 3.2, y6 + 3.2, 3.2, fill=1, stroke=0)
    c.setFont(SEM, 8.8); c.setFillColor(PLUM)
    c.drawString(ML + 15, y6, h2)
    y6 = para(c, b2, ML + 15, y6 - 13, CW - 15, size=8, lead=11.2,
              color=PLUM_60) - 8

# ================================================================ 12 AI PIPELINE
y = d.newpage()
y = eyebrow(c, "08  AI pipeline", ML, y)
y = title(c, "Every AI feature is built the same way", ML, y - 6)
y = lede(c, "Twelve services share one shape. That is deliberate: a single "
            "pipeline means a single place to add a guard.", ML, y - 10)
y -= 28

stages = [
    ("Gather", "The user's field, seniority, skills and target roles, plus the "
               "feature's own input — a posting, a resume, an answer."),
    ("Build", "One shared context block is injected into every prompt, so the "
              "output fits the user's discipline rather than defaulting to "
              "software."),
    ("Call", "gpt-4o-mini through one endpoint helper, with strict JSON schema "
             "mode, a token ceiling, and a 30-second timeout."),
    ("Parse", "Never-throws parsing. Malformed output degrades to a safe empty "
              "result rather than raising."),
    ("Guard", "Domain rules applied after parsing: enum values validated, scores "
              "clamped, empty praise filtered, duplicates rejected."),
    ("Persist", "Written scoped to the user, and rendered back as a partial the "
                "client swaps into the page."),
]
bw4 = (CW - 5 * 8) / 6
for i, (t, _) in enumerate(stages):
    x = ML + i * (bw4 + 8)
    boxtext(c, x, y - 40, bw4, 40, t, fill=BLUE_30, stroke=None, size=8.6,
            r=8, num=i + 1)
    if i < 5:
        arrow(c, x + bw4 + 1, y - 20, x + bw4 + 7, y - 20, head=3.6)
y -= 56

for i, (t, b) in enumerate(stages):
    c.setFont(SEM, 8.6); c.setFillColor(PLUM)
    c.drawString(ML, y, f"{i+1}.  {t}")
    ny = para(c, b, ML + 62, y, CW - 62, size=8.6, lead=12.4, color=PLUM_60)
    y = ny - 7

y -= 10
CARD_H = 152
rbox(c, ML, y - CARD_H, CW, CARD_H, fill=WHITE, stroke=HAIR, r=12)
c.setFont(SEM, 9.6); c.setFillColor(PLUM)
c.drawString(ML + 18, y - 26, "Two budgets, not one")
gy = para(c, "AI calls draw on two separate per-user allowances. Practice is "
             "many-small-calls by nature; resume parsing and cover letters are "
             "rare and expensive. Sharing one budget meant a practice round could "
             "lock the resume tools, so they were split.",
          ML + 18, y - 46, CW - 36, size=8.6, lead=12.4, color=PLUM_60)

ty = gy - 16
for lab, off in [("Bucket", 0), ("Demo account", 214), ("Signed in", 336)]:
    c.setFont(SEM, 7.2); c.setFillColor(PLUM_40)
    c.drawString(ML + 18 + off, ty, " ".join(lab.upper()))
ty -= 7
rule(c, ML + 18, ty, CW - 36)
ty -= 14
for a, b_, cc in [("Practice  (generate + score)", "25 / hour",
                   "100 / rolling 24h"),
                  ("Everything else", "10 / hour", "20 / hour")]:
    c.setFont(MED, 8); c.setFillColor(PLUM)
    c.drawString(ML + 18, ty, a)
    c.setFont(REG, 8); c.setFillColor(PLUM_60)
    c.drawString(ML + 18 + 214, ty, b_)
    c.drawString(ML + 18 + 336, ty, cc)
    ty -= 15

y -= CARD_H + 26
c.setFont(SEM, 9.6); c.setFillColor(PLUM)
c.drawString(ML, y, "Why a small model")
y -= 16
y = para(c, "gpt-4o-mini is the only model the app calls. Practice generation and "
            "answer scoring are many small calls rather than a few large ones, so "
            "per-call cost matters more than peak capability. Strict JSON schema "
            "output removes string repair from the parsing path entirely, which is "
            "worth more here than a larger model's prose.",
         ML, y, CW, size=8.8, lead=12.8, color=PLUM_60)
y -= 16
guards = [("max_tokens", "on all 12 services"),
          ("30-second timeout", "on all 12 services"),
          ("Never-throws parsing", "degrades to an empty result"),
          ("One endpoint helper", "a test fails if the address is hardcoded")]
gbw = (CW - 3 * 12) / 4
for i, (t, s2) in enumerate(guards):
    x = ML + i * (gbw + 12)
    rbox(c, x, y - 50, gbw, 50, fill=BLUE_30, stroke=None, r=10)
    c.setFont(SEM, 8.2); c.setFillColor(PLUM)
    for j, ln in enumerate(wrap(t, SEM, 8.2, gbw - 24)):
        c.drawString(x + 12, y - 19 - j * 10, ln)
    c.setFont(REG, 6.8); c.setFillColor(tint(PLUM, 0.3))
    for j, ln in enumerate(wrap(s2, REG, 6.8, gbw - 24)):
        c.drawString(x + 12, y - 33 - j * 8.6, ln)

# ================================================================ 13 FIELD AWARE
y = d.newpage()
y = eyebrow(c, "09  Field awareness", ML, y)
y = title(c, "The app is not a software-only tool", ML, y - 6)
y = lede(c, "The original version assumed every user was a developer. Prompt "
            "vocabulary, the role suggestions and the resume parser all had it "
            "baked in. A nursing or accounting student got output that didn't fit "
            "their discipline.", ML, y - 10)
y -= 30

# before / after
half = (CW - 22) / 2
rbox(c, ML, y - 150, half, 150, fill=WHITE, stroke=HAIR, r=12)
c.setFont(SEM, 7.4); c.setFillColor(PLUM_40)
c.drawString(ML + 18, y - 24, " ".join("BEFORE"))
c.setFont(SEM, 10.4); c.setFillColor(PLUM)
c.drawString(ML + 18, y - 44, "Implicitly software-only")
by = y - 64
for t in ["Prompts said \"technical skills\", \"codebase\", \"tech stack\"",
          "Target-role suggestions were a hardcoded IT array",
          "The resume parser's example role was \"Backend Developer Intern\"",
          "A nursing resume produced developer questions"]:
    by = bullet(c, t, ML + 18, by, half - 36, size=8.2, lead=11.4,
                color=PLUM_60, dot=HAIR) - 3

rbox(c, ML + half + 22, y - 150, half, 150, fill=BLUE_30, stroke=None, r=12)
c.setFont(SEM, 7.4); c.setFillColor(PLUM_60)
c.drawString(ML + half + 40, y - 24, " ".join("AFTER"))
c.setFont(SEM, 10.4); c.setFillColor(PLUM)
c.drawString(ML + half + 40, y - 44, "Adapts to the user's field")
ay = y - 64
for t in ["Field and category stored on the profile, detected from the resume",
          "One context builder injects it into every AI prompt",
          "Role suggestions keyed to 13 field categories",
          "A nursing resume produces clinical questions"]:
    ay = bullet(c, t, ML + half + 40, ay, half - 36, size=8.2, lead=11.4,
                color=PLUM, dot=PLUM) - 3
y -= 172

# flow
boxtext(c, ML, y - 44, 118, 44, "Resume", sub="PDF or Word", fill=CREAM_50,
        stroke=None, size=9)
arrow(c, ML + 121, y - 22, ML + 141, y - 22)
boxtext(c, ML + 144, y - 44, 118, 44, "Parse", sub="field + category detected",
        fill=BLUE_30, stroke=None, size=9)
arrow(c, ML + 265, y - 22, ML + 285, y - 22)
boxtext(c, ML + 288, y - 44, 118, 44, "Review", sub="user confirms",
        fill=BLUE_30, stroke=None, size=9)
arrow(c, ML + 409, y - 22, ML + 429, y - 22)
boxtext(c, ML + 432, y - 44, 64, 44, "Profile", fill=tint(PLUM, 0.62),
        stroke=None, size=9)
c.setStrokeColor(PLUM_60); c.setLineWidth(0.9)
c.line(ML + 464, y - 47, ML + 464, y - 62)
c.line(ML + 464, y - 62, ML + 60, y - 62)
arrow(c, ML + 60, y - 62, ML + 60, y - 78)
alabel(c, "feeds every AI prompt", ML + 262, y - 60, size=6.8)
y -= 84

boxtext(c, ML, y - 40, CW, 40,
        "12 AI services  ·  analyzer  ·  resume match  ·  cover letter  ·  "
        "prep  ·  practice  ·  scoring  ·  salary  ·  follow-up",
        fill=tint(BLUE, 0.35), stroke=None, size=8.4, r=10)

y -= 70
c.setFont(SEM, 9.6); c.setFillColor(PLUM)
c.drawString(ML, y, "The same button, two disciplines")
y -= 16
y = para(c, "Nothing in the code branches on field. One context block changes "
            "what comes back.", ML, y, CW, size=8.8, lead=12.6, color=PLUM_60)
y -= 18
ex = [("Technology", "\u201cIn what scenarios would you choose a binary search "
                     "tree over a hash table for data storage and retrieval?\u201d"),
      ("Healthcare", "\u201cIn a mass-casualty incident, how would you "
                     "prioritise patient care compared with a routine "
                     "emergency?\u201d")]
ebw = (CW - 18) / 2
for i, (f, q) in enumerate(ex):
    x = ML + i * (ebw + 18)
    rbox(c, x, y - 84, ebw, 84, fill=WHITE, stroke=HAIR, r=11)
    c.setFillColor(BLUE); c.roundRect(x + 16, y - 26, 62, 14, 4, fill=1,
                                      stroke=0)
    c.setFont(SEM, 6.6); c.setFillColor(PLUM)
    c.drawCentredString(x + 47, y - 21.8, f.upper())
    para(c, q, x + 16, y - 42, ebw - 32, font=LGT, size=9.2, lead=13,
         color=PLUM)
c.setFont(REG, 7.4); c.setFillColor(PLUM_40)
c.drawString(ML, y - 98, "Both produced by the same endpoint, on the same day, "
                         "differing only in the profile behind them.")

# ================================================================ 14-15 PRACTICE
y = d.newpage()
y = eyebrow(c, "10  The practice engine", ML, y)
y = title(c, "Generating questions is easy. Not repeating them is not.", ML, y - 6)
y = lede(c, "The hard problem is that a language model asked not to repeat itself "
            "will rephrase instead. Three layers, and only two of them work.",
         ML, y - 10)
y -= 28

lay = [
    ("Layer 1", "Topic steering",
     "The prompt carries every topic already covered and asks for new ones."),
    ("Layer 2", "TopicKey",
     "Normalised topic comparison with subset containment, applied in code."),
    ("Layer 3", "Top-up retry",
     "One extra call for the shortfall when duplicates are dropped. Capped."),
]
bw5 = (CW - 2 * 14) / 3
for i, (n, t, b) in enumerate(lay):
    x = ML + i * (bw5 + 14)
    fill = BLUE_30 if i == 1 else WHITE
    stroke = None if i == 1 else HAIR
    rbox(c, x, y - 98, bw5, 98, fill=fill, stroke=stroke, r=12)
    c.setFont(SEM, 7); c.setFillColor(PLUM_40)
    c.drawString(x + 14, y - 22, " ".join(n.upper()))
    c.setFont(SEM, 10.4); c.setFillColor(PLUM)
    c.drawString(x + 14, y - 40, t)
    para(c, b, x + 14, y - 56, bw5 - 28, size=8, lead=11.4, color=PLUM_60)
y -= 118

rbox(c, ML, y - 128, CW, 128, fill=CREAM_50, stroke=None, r=12)
c.setFont(SEM, 9.6); c.setFillColor(PLUM)
c.drawString(ML + 18, y - 26, "What two instrumented live runs measured")
my = y - 46
para(c, "Thirty questions generated in one topic area, twice, with every batch "
        "logged. The result inverted the design's own assumption.",
     ML + 18, my, CW - 36, size=8.6, lead=12.2, color=PLUM_60)
my -= 30

mrows = [("QuestionHash  (string-level)", "fired 0 times in 84 questions"),
         ("TopicKey  (normalised topics)", "caught 4 duplicates in 30"),
         ("Prompt exclusion list", "no measurable effect on recurrence"),
         ("Topic granularity", "held at every batch — the rule works")]
for a, b_ in mrows:
    c.setFont(MED, 8.4); c.setFillColor(PLUM)
    c.drawString(ML + 18, my, a)
    c.setFont(REG, 8.4); c.setFillColor(PLUM_60)
    c.drawString(ML + 250, my, b_)
    my -= 15

y -= 146
rbox(c, ML, y - 60, CW, 60, fill=PLUM, stroke=None, r=12)
c.setFont(SEM, 7.4); c.setFillColor(BLUE)
c.drawString(ML + 18, y - 22, " ".join("THE FINDING"))
para(c, "The prompt does not steer. The mechanism carries de-duplication almost "
        "entirely. Anyone reaching for the prompt to reduce repetition is "
        "reaching for the wrong lever.",
     ML + 18, y - 38, CW - 36, size=9, lead=13, color=tint(CREAM, 0.12))

y -= 84
c.setFont(SEM, 9.6); c.setFillColor(PLUM)
c.drawString(ML, y, "What one request actually does")
y -= 26
fun = [("Ask for 7", "over-request by two"),
       ("Topic filter", "reject known topics"),
       ("Hash filter", "unique index on write"),
       ("Top-up", "one retry, capped"),
       ("Store 5", "report what was dropped")]
fbw = (CW - 4 * 16) / 5
for i, (t, s2) in enumerate(fun):
    x = ML + i * (fbw + 16)
    boxtext(c, x, y - 50, fbw, 50, t, sub=s2,
            fill=BLUE_30 if i in (1, 2) else CREAM_50, stroke=None,
            size=8.6, subsize=6.3, r=9, num=i + 1)
    if i < 4:
        arrow(c, x + fbw + 2.5, y - 25, x + fbw + 13.5, y - 25, head=4.0)
y -= 64
c.setFont(REG, 7.8); c.setFillColor(PLUM_40)
c.drawString(ML, y, "Every generation logs what it asked for, what came back, "
                    "and which layer dropped what — so a count that does not move "
                    "is diagnosable in one line.")

# --- page 15
y = d.newpage()
y = eyebrow(c, "10  The practice engine  ·  continued", ML, y)
y = title(c, "Answering, scoring and the loop back", ML, y - 6)
y -= 8

steps2 = [
    ("Answer", "40-character minimum, drafts autosaved to the browser and "
               "restored on reload, elapsed time recorded."),
    ("Score", "One to five, with what worked, exactly two things to change, "
              "what was left out, and a stronger opening line."),
    ("Retry", "The earlier attempt is kept and shown beside the new one, "
              "oldest first, so the progression reads left to right."),
    ("Track", "Progress by difficulty, average score, weakest topic — each "
              "with a minimum sample size before it will say anything."),
]
bw6 = (CW - 3 * 12) / 4
for i, (t, b) in enumerate(steps2):
    x = ML + i * (bw6 + 12)
    rbox(c, x, y - 128, bw6, 128, fill=WHITE, stroke=HAIR, r=12)
    c.setFillColor(BLUE); c.circle(x + 20, y - 26, 9, fill=1, stroke=0)
    c.setFont(SEM, 8); c.setFillColor(PLUM)
    c.drawCentredString(x + 20, y - 28.8, str(i + 1))
    c.setFont(SEM, 10); c.setFillColor(PLUM)
    c.drawString(x + 14, y - 52, t)
    para(c, b, x + 14, y - 70, bw6 - 28, size=7.8, lead=11, color=PLUM_60)
y -= 150

rbox(c, ML, y - 112, CW, 112, fill=BLUE_30, stroke=None, r=12)
c.setFont(SEM, 9.6); c.setFillColor(PLUM)
c.drawString(ML + 18, y - 26, "The scoring rule that worked, and the one that didn't")
sy2 = y - 48
para(c, "The first attempt tightened the prose around the grade boundaries. "
        "Measured against a deliberately generic answer and a specific one, it "
        "changed nothing — the junk answer still scored 3. It was removed rather "
        "than kept.",
     ML + 18, sy2, CW - 36, size=8.6, lead=12.2, color=PLUM_60)
sy2 -= 40
para(c, "The rule that worked names a condition the model can check rather than "
        "a judgement it has to make: scan for a number, a date, a named tool, a "
        "named role, or a situation that actually happened. If there is none, "
        "at most 2. The junk answer dropped to 2; the specific answer held at 4.",
     ML + 18, sy2, CW - 36, size=8.6, lead=12.2, color=PLUM)

y -= 134
c.setFont(SEM, 9.6); c.setFillColor(PLUM)
c.drawString(ML, y, "Statistics that refuse to speak too early")
y -= 16
y = para(c, "Every derived figure carries a minimum sample size. A weakest topic "
            "needs at least three answers before it will be named; a score trend "
            "needs twenty. Below those thresholds the panel shows nothing rather "
            "than presenting noise as insight.", ML, y, CW, size=8.8, lead=12.6,
         color=PLUM_60)
y -= 18
th = [("3", "answers before a weakest topic is named"),
      ("10", "answers before the recent-average window appears"),
      ("20", "answers before a trend line is drawn")]
tbw = (CW - 2 * 12) / 3
for i, (n, lab) in enumerate(th):
    x = ML + i * (tbw + 12)
    rbox(c, x, y - 56, tbw, 56, fill=CREAM_50, stroke=None, r=11)
    c.setFont(BLD, 20); c.setFillColor(PLUM)
    c.drawString(x + 14, y - 30, n)
    c.setFont(REG, 7.4); c.setFillColor(PLUM_60)
    for j, ln in enumerate(wrap(lab, REG, 7.4, tbw - 28)):
        c.drawString(x + 14, y - 42 - j * 9.2, ln)

# ================================================================ 16 SECURITY
y = d.newpage()
y = eyebrow(c, "11  Security & data handling", ML, y)
y = title(c, "Scoped by construction, deletable on request", ML, y - 6)
y -= 12

secs = [
    ("Authentication",
     "ASP.NET Core Identity with cookie authentication. Password reset by "
     "emailed token. Optional Google OAuth for the read-only Gmail connection "
     "only — never for sign-in."),
    ("Per-user scoping",
     "Every domain query filters on the authenticated user's id. A dedicated "
     "test class attempts cross-user access on every write route and asserts a "
     "404 that contains none of the other user's data."),
    ("Secrets",
     "No key, connection string or credential is committed. Local development "
     "uses .NET user-secrets; production reads environment variables. The e2e "
     "environment cannot read user-secrets at all, so it structurally cannot "
     "spend against the real API key."),
    ("Rate limiting",
     "Two per-user budgets across every AI path, including work started outside "
     "a request. Every AI client carries a 30-second timeout and a token ceiling."),
    ("Gmail scope",
     "One scope is requested: read-only. Message bodies are classified but never "
     "stored; only matched metadata and a one-line summary are kept."),
    ("Deletion",
     "Account deletion revokes the Google grant before wiping data, then clears "
     "every user-owned table. A test seeds one row in every table, deletes "
     "through the real page, and asserts each table is empty — and fails if a "
     "new table was never seeded."),
    ("Transport and headers",
     "HTTPS end to end behind a proxied CNAME. Security headers are set centrally, "
     "with a content security policy currently in report-only mode pending the "
     "removal of the last inline scripts."),
]
for h, b in secs:
    c.setFillColor(BLUE); c.circle(ML + 3.4, y + 3.4, 3.4, fill=1, stroke=0)
    c.setFont(SEM, 9.4); c.setFillColor(PLUM)
    c.drawString(ML + 16, y, h)
    y = para(c, b, ML + 16, y - 16, CW - 16, size=8.8, lead=13.2,
             color=PLUM_60) - 15

y -= 6
rbox(c, ML, y - 80, CW, 80, fill=CREAM_50, stroke=None, r=12)
c.setFont(SEM, 9.4); c.setFillColor(PLUM)
c.drawString(ML + 18, y - 24, "What is never stored")
ny2 = y - 42
for t in ["Gmail message bodies — classified in memory, never written",
          "Any API key, token or connection string in source control",
          "Another user's data in any response, including error pages"]:
    ny2 = bullet(c, t, ML + 18, ny2, CW - 36, size=8.4, lead=11.6,
                 color=PLUM_60, dot=PLUM) - 2

# ================================================================ 17 DEPLOY
y = d.newpage()
y = eyebrow(c, "12  Deployment", ML, y)
y = title(c, "Local SQLite to production PostgreSQL", ML, y - 6)
y = lede(c, "One Dockerfile, one Railway service, one database, one region. "
            "Every schema change is verified against real PostgreSQL before it "
            "ships.", ML, y - 10)
y -= 34

pipe = [
    ("Local", "SQLite  ·  user-secrets  ·  dotnet run", CREAM_50),
    ("GitHub", "push  ·  pull request  ·  CI", BLUE_30),
    ("CI", "build  ·  1,392 xUnit tests", BLUE_30),
    ("Docker", "multi-stage build  ·  .NET 9 runtime", BLUE_30),
    ("Railway", "EU West  ·  1 replica  ·  volume", tint(BLUE, 0.35)),
    ("Domain", "custom domain  ·  TLS", tint(PLUM, 0.62)),
]
bw7 = (CW - 5 * 14) / 6
for i, (t, s, col) in enumerate(pipe):
    x = ML + i * (bw7 + 14)
    boxtext(c, x, y - 62, bw7, 62, t, sub=s, fill=col, stroke=None,
            size=8.8, subsize=6.2, r=9)
    if i < 5:
        arrow(c, x + bw7 + 2.5, y - 31, x + bw7 + 11.5, y - 31, head=4.0)
y -= 84

half2 = (CW - 22) / 2
rbox(c, ML, y - 132, half2, 132, fill=WHITE, stroke=HAIR, r=12)
c.setFont(SEM, 9.6); c.setFillColor(PLUM)
c.drawString(ML + 18, y - 26, "Why one replica")
para(c, "A volume is attached for uploaded resumes, and Railway does not allow "
        "replicas on a service with an attached volume. That makes single-instance "
        "a structural property rather than a setting — which matters, because the "
        "rate limiter holds its buckets in memory per instance.",
     ML + 18, y - 46, half2 - 36, size=8.4, lead=12, color=PLUM_60)

rbox(c, ML + half2 + 22, y - 132, half2, 132, fill=CREAM_50, stroke=None, r=12)
c.setFont(SEM, 9.6); c.setFillColor(PLUM)
c.drawString(ML + half2 + 40, y - 26, "Two providers, one schema")
para(c, "Migrations must apply cleanly on both SQLite and PostgreSQL. A test "
        "reflects over every column and fails on any CLR type it has not seen, "
        "so a new type cannot reach production untested. Another asserts no "
        "column becomes a length-limited varchar on PostgreSQL.",
     ML + half2 + 40, y - 46, half2 - 36, size=8.4, lead=12, color=PLUM_60)
y -= 152

rbox(c, ML, y - 60, CW, 60, fill=BLUE_30, stroke=None, r=12)
c.setFont(SEM, 8.6); c.setFillColor(PLUM)
c.drawString(ML + 18, y - 22, "The demo account")
para(c, "Publicly reachable, shared, and reset both per session and nightly at "
        "04:00 UTC. Practice data, the resume draft and the profile all return to "
        "a fixed seeded state, so one visitor never inherits another's answers.",
     ML + 18, y - 38, CW - 36, size=8.4, lead=12, color=PLUM_60)

# ================================================================ 18 TESTING
y = d.newpage()
y = eyebrow(c, "13  Testing", ML, y)
y = title(c, "1,392 tests, and an honest account of the gap", ML, y - 6)
y -= 14

tcats = [
    ("Unit", "Pure logic with no I/O: hash normalisation, topic keys, attempt "
             "history, progress arithmetic, resume file-type detection, parsed-"
             "profile validation."),
    ("Integration", "In-memory host with a fake model transport. Full request "
                    "paths: generation, scoring, review and merge, rate limits, "
                    "grouping, dashboard aggregation."),
    ("Ownership", "Every write route attempted as the wrong user, asserting a "
                  "404 whose body contains none of the other user's seeded data."),
    ("Provider parity", "The same query run against SQLite and real PostgreSQL, "
                        "asserting identical results — and asserting the absolute "
                        "expected order, not only that the two agree."),
    ("Migration shape", "Reflection over every column, failing on an unrecognised "
                        "CLR type or a length-limited varchar."),
    ("Guard tests", "Deliberate mutations confirm each test fails when the thing "
                    "it guards is removed."),
]
for h, b in tcats:
    c.setFont(SEM, 9.4); c.setFillColor(PLUM)
    c.drawString(ML, y, h)
    y = para(c, b, ML + 104, y, CW - 104, size=8.6, lead=12.2,
             color=PLUM_60) - 10

y -= 6
rbox(c, ML, y - 116, CW, 116, fill=CREAM_50, stroke=None, r=12)
c.setFont(SEM, 9.6); c.setFillColor(PLUM)
c.drawString(ML + 18, y - 26, "What the suite does not cover")
gy2 = y - 46
para(c, "Client-side behaviour has no automated coverage. The batch scoring "
        "fan-out, draft autosave, collapse state and the tour engine are verified "
        "by hand in a real browser. That gap is not theoretical: three bugs this "
        "month were invisible to a fully green suite.",
     ML + 18, gy2, CW - 36, size=8.6, lead=12.2, color=PLUM_60)
gy2 -= 46
for t in ["A progress card that never refreshed after generating",
          "Draft autosave that silently never restored",
          "A rate-limit message rendering 2,786px above the viewport"]:
    gy2 = bullet(c, t, ML + 18, gy2, CW - 36, size=8.2, lead=11.4,
                 color=PLUM, dot=PLUM) - 1
y -= 132
c.setFont(REG, 8.2); c.setFillColor(PLUM_40)
c.drawString(ML, y, "An end-to-end browser suite covering these paths is planned "
                    "and scoped. It is not built.")
y -= 34
c.setFont(SEM, 9.6); c.setFillColor(PLUM)
c.drawString(ML, y, "Where the tests actually run")
y -= 24
envs2 = [("Local", "SQLite in-memory  ·  fake model transport  ·  ~60 seconds"),
         ("Real PostgreSQL", "migration shape and provider parity, opt-in by "
                             "connection string"),
         ("CI", "every push and pull request to the default branch")]
for t, b in envs2:
    rbox(c, ML, y - 40, CW, 40, fill=WHITE, stroke=HAIR, r=10)
    c.setFillColor(BLUE); c.roundRect(ML, y - 40, 3.5, 40, 1.6, fill=1, stroke=0)
    c.setFont(SEM, 8.8); c.setFillColor(PLUM)
    c.drawString(ML + 18, y - 17, t)
    c.setFont(REG, 8.2); c.setFillColor(PLUM_60)
    c.drawString(ML + 18, y - 31, b)
    y -= 50

# ================================================================ 19-20 NOTES
y = d.newpage()
y = eyebrow(c, "14  Engineering notes", ML, y)
y = title(c, "Three findings worth reading", ML, y - 6)
y = lede(c, "Each of these started as a confident assumption and was overturned "
            "by measurement.", ML, y - 10)
y -= 28

note1 = ("01", "Production and local disagreed about sorting — for months",
         "Local PostgreSQL was initialised with collation C, which is byte order, "
         "the same as SQLite. Production runs en_US.utf8, which is dictionary "
         "order. Sorting applications by company name therefore produced a "
         "different order in production than on any developer machine, silently, "
         "for as long as the feature had existed.",
         "The fix was to lower the value in the query, so ordering no longer "
         "depends on a database setting the repository does not control. The "
         "sharper lesson: a parity test asserting only that two providers agree "
         "would have passed here and proved nothing, because both agreed on the "
         "wrong answer. The test now asserts the absolute expected order.")
note2 = ("02", "The prompt was not the lever",
         "Question de-duplication was designed with prompt steering as the "
         "primary mechanism and code-level hashing as a backstop. Two "
         "instrumented live runs measured the opposite: the hash never fired "
         "across 84 questions, while normalised topic comparison caught four "
         "duplicates in thirty.",
         "A second measurement refined it further. A prompt asking the model to "
         "make a judgement does not land; a prompt naming a condition the model "
         "can check does. \"A generic answer is a 2\" changed nothing. \"Scan for "
         "a number, a date, a named tool, or a situation that happened — if there "
         "is none, at most 2\" moved the score exactly as intended, without "
         "dragging good answers down with it.")

for num, h, b1, b2 in (note1, note2):
    rbox(c, ML, y - 232, CW, 232, fill=WHITE, stroke=HAIR, r=12)
    c.setFillColor(BLUE)
    c.roundRect(ML, y - 232, 3.5, 232, 1.6, fill=1, stroke=0)
    c.setFont(BLD, 20); c.setFillColor(tint(BLUE, 0.15))
    c.drawString(ML + 22, y - 32, num)
    c.setFont(SEM, 11.6); c.setFillColor(PLUM)
    ny = y - 30
    for ln in wrap(h, SEM, 11.6, CW - 90):
        c.drawString(ML + 56, ny, ln); ny -= 15
    ny -= 8
    ny = para(c, b1, ML + 22, ny, CW - 44, size=9, lead=13.8, color=PLUM_60)
    ny -= 10
    para(c, b2, ML + 22, ny, CW - 44, size=9, lead=13.8, color=PLUM)
    y -= 248

# --- page 20
y = d.newpage()
y = eyebrow(c, "14  Engineering notes  ·  continued", ML, y)
y -= 4

rbox(c, ML, y - 214, CW, 214, fill=WHITE, stroke=HAIR, r=12)
c.setFillColor(BLUE); c.roundRect(ML, y - 214, 3.5, 214, 1.6, fill=1, stroke=0)
c.setFont(BLD, 20); c.setFillColor(tint(BLUE, 0.15))
c.drawString(ML + 22, y - 32, "03")
c.setFont(SEM, 11.6); c.setFillColor(PLUM)
ny = y - 30
for ln in wrap("A safety net was hiding the thing it sat next to", SEM, 11.6,
               CW - 90):
    c.drawString(ML + 56, ny, ln); ny -= 15
ny -= 8
ny = para(c, "A draft-autosave feature silently never restored. A try/catch "
             "written to tolerate private-mode browsers, where local storage "
             "throws, was broad enough to also swallow a ReferenceError from an "
             "unrelated declaration-order bug in the same file. Every server-side "
             "test passed throughout.",
          ML + 22, ny, CW - 44, size=8.6, lead=12.4, color=PLUM_60)
ny -= 8
ny = para(c, "A sweep of the codebase found fifteen more multi-statement catch "
             "blocks, including one wrapping thirty-nine statements that answered "
             "every possible failure with \"check your connection\" — so a "
             "rendering bug would have sent a user to their network settings.",
          ML + 22, ny, CW - 44, size=8.6, lead=12.4, color=PLUM_60)
ny -= 8
para(c, "A shared guard now re-throws ReferenceError, which is always a "
        "programming error, while still passing through TypeError and SyntaxError "
        "— the two ways fetch and JSON.parse legitimately report the conditions "
        "those catches were written for. A blanket re-throw would have traded "
        "silent failures for noisy false alarms.",
     ML + 22, ny, CW - 44, size=8.6, lead=12.4, color=PLUM)
y -= 236

rbox(c, ML, y - 106, CW, 106, fill=PLUM, stroke=None, r=12)
c.setFont(SEM, 7.4); c.setFillColor(BLUE)
c.drawString(ML + 22, y - 26, " ".join("THE PATTERN UNDERNEATH ALL THREE"))
para(c, "In each case a check passed while the thing it was supposed to observe "
        "was broken — a parity test where both sides agreed on the wrong answer, "
        "a reflection test that could only see attributes, a catch block that "
        "swallowed the error it was meant to surface. The habit that caught them "
        "was asking what a green result is structurally incapable of proving.",
     ML + 22, y - 46, CW - 44, font=LGT, size=10.4, lead=15.6,
     color=tint(CREAM, 0.1))

# ================================================================ 21 STACK
y = d.newpage()
y = eyebrow(c, "15  Tech stack", ML, y)
y = title(c, "Every choice, and why", ML, y - 6)
y -= 18

stack = [
    ("ASP.NET Core 9 MVC", "Server-rendered Razor",
     "Server rendering keeps one language and one deployable. No SPA build step, "
     "no client state to keep in sync."),
    ("EF Core 9", "ORM and migrations",
     "Migrations as code, and one model definition that both providers share."),
    ("PostgreSQL 18", "Production database",
     "Managed on Railway, with backups and a real type system worth targeting."),
    ("SQLite", "Local development",
     "Zero-setup local runs and a fast in-memory database for the test suite."),
    ("OpenAI gpt-4o-mini", "Language model",
     "Cheap enough for many small calls, with strict JSON schema output that "
     "removes string repair from the parsing path."),
    ("ASP.NET Core Identity", "Authentication",
     "Password hashing, token handling and cookie management that should not be "
     "hand-rolled."),
    ("xUnit", "Testing",
     "Integration tests run the real request pipeline against an in-memory host."),
    ("Docker", "Packaging",
     "A multi-stage build so the runtime image carries no SDK."),
    ("Railway", "Hosting",
     "Deploys from a Dockerfile, provisions PostgreSQL, and handles TLS on a "
     "custom domain."),
    ("Cloudflare", "DNS",
     "Proxied CNAME to the Railway service."),
    ("Resend", "Transactional email",
     "Password reset only. No marketing, no lists."),
    ("Vanilla JavaScript", "Client behaviour",
     "Partials rendered on the server and swapped in. No framework, because "
     "nothing on the page needs one."),
]
c.setFont(SEM, 7.2); c.setFillColor(PLUM_40)
c.drawString(ML, y, " ".join("TECHNOLOGY"))
c.drawString(ML + 128, y, " ".join("ROLE"))
c.drawString(ML + 238, y, " ".join("WHY"))
y -= 8
rule(c, ML, y, CW, color=PLUM_40, lw=0.7)
y -= 16

for t, r_, w_ in stack:
    lines = wrap(w_, REG, 8.2, CW - 238)
    rowh = max(len(lines) * 11.8, 13) + 14
    c.setFont(SEM, 8.4); c.setFillColor(PLUM)
    c.drawString(ML, y, t)
    c.setFont(REG, 8.2); c.setFillColor(PLUM_60)
    for j, ln in enumerate(wrap(r_, REG, 8.2, 104)):
        c.drawString(ML + 128, y - j * 11, ln)
    c.setFont(REG, 8.2); c.setFillColor(PLUM_60)
    for j, ln in enumerate(lines):
        c.drawString(ML + 238, y - j * 11.8, ln)
    y -= rowh
    rule(c, ML, y + 5, CW)

# ================================================================ 22 ROADMAP
y = d.newpage()
y = eyebrow(c, "16  Roadmap", ML, y)
y = title(c, "Planned — not built", ML, y - 6)
y = lede(c, "Everything on this page is scoped and deliberately deferred. None of "
            "it exists in the codebase today.", ML, y - 10)
y -= 30

road = [
    ("End-to-end browser suite",
     "Practice and tour dimensions driving a real browser, closing the "
     "client-side coverage gap named on page 18. Scoped, with the specific bugs "
     "each check would have caught already written down."),
    ("Content security policy enforcement",
     "Five inline scripts remain, all in two layout files. Moving them to files "
     "allows the policy to move from report-only to enforced."),
    ("Generator consolidation",
     "Interview prep and practice currently run two generators against one "
     "question store. Folding them into one removes a second prompt to maintain."),
    ("Practice history view",
     "Deliberately deferred until there is enough data for it to say anything: "
     "forty or more answered questions, three topics with at least three answers "
     "each, and retries becoming common."),
]
for h, b in road:
    rbox(c, ML, y - 94, CW, 94, fill=WHITE, stroke=HAIR, r=12, dash=(3, 3))
    c.setFillColor(CREAM); c.roundRect(ML + 18, y - 28, 52, 15, 4, fill=1,
                                       stroke=0)
    c.setFont(SEM, 6.8); c.setFillColor(shade(PLUM, 0.1))
    c.drawCentredString(ML + 44, y - 23.6, "PLANNED")
    c.setFont(SEM, 10.4); c.setFillColor(PLUM)
    c.drawString(ML + 80, y - 24, h)
    para(c, b, ML + 18, y - 48, CW - 36, size=8.6, lead=12.6, color=PLUM_60)
    y -= 108

y -= 2
rbox(c, ML, y - 70, CW, 70, fill=CREAM_50, stroke=None, r=12)
c.setFont(SEM, 9); c.setFillColor(PLUM)
c.drawString(ML + 18, y - 22, "Decided against, and recorded as such")
para(c, "A pre-generated question seed bank was scoped and rejected: roughly 936 "
        "model calls for 4,680 questions that could not be quality-checked, and "
        "global rows that would fight a deliberately non-nullable user id. "
        "Documented as a decision, not a backlog item.",
     ML + 18, y - 38, CW - 36, size=8.2, lead=11.6, color=PLUM_60)

# ================================================================ 23 APPENDIX
y = d.newpage()
y = eyebrow(c, "17  Appendix", ML, y)
y = title(c, "Routes and configuration", ML, y - 6)
y -= 18

c.setFont(SEM, 8.6); c.setFillColor(PLUM)
c.drawString(ML, y, "Principal routes")
y -= 16
c.setFont(SEM, 6.8); c.setFillColor(PLUM_40)
c.drawString(ML, y, " ".join("METHOD"))
c.drawString(ML + 54, y, " ".join("PATH"))
c.drawString(ML + 250, y, " ".join("PURPOSE"))
y -= 7
rule(c, ML, y, CW, color=PLUM_40, lw=0.7)
y -= 13

routes = [
    ("GET", "/", "Landing page with live preview"),
    ("GET", "/Home/Dashboard", "Stats, attention, inbox, practice summary"),
    ("GET", "/JobApplications", "List and board views"),
    ("POST", "/JobApplications/Create", "Add an application"),
    ("POST", "/Analyzer/Analyze", "Extract skills from a posting"),
    ("POST", "/Profile/UploadResume", "Upload and parse a resume"),
    ("GET", "/Profile/ReviewResume", "Confirm parsed data before merge"),
    ("POST", "/Profile/ApplyResumeReview", "Merge confirmed data into profile"),
    ("POST", "/Profile/ScoreResume", "Score resume against a posting"),
    ("POST", "/CoverLetter/Generate", "Draft a cover letter"),
    ("GET", "/InterviewPrep/Prep", "Posting-grounded questions"),
    ("POST", "/InterviewPrep/Generate", "Generate more prep questions"),
    ("GET", "/Practice", "Practice page with filters and progress"),
    ("POST", "/Practice/GenerateMore", "Generate questions"),
    ("POST", "/Practice/SubmitAnswer", "Score an answer"),
    ("GET", "/Practice/Progress", "Refreshed progress partial"),
    ("POST", "/Practice/DeleteAll", "Delete all practice data"),
    ("GET", "/Home/Privacy", "Privacy policy"),
    ("GET", "/Home/Terms", "Terms"),
    ("GET", "/health", "Health check for the platform"),
]
for m, p, pu in routes:
    col = BLUE if m == "GET" else tint(PLUM, 0.45)
    rbox(c, ML, y - 2.4, 30, 10, fill=col, stroke=None, r=3)
    c.setFont(SEM, 5.9); c.setFillColor(PLUM if m == "GET" else WHITE)
    c.drawCentredString(ML + 15, y + 0.6, m)
    c.setFont(REG, 7.6); c.setFillColor(PLUM)
    c.drawString(ML + 54, y, p)
    c.setFillColor(PLUM_60)
    c.drawString(ML + 250, y, pu)
    y -= 13.6

y -= 10
c.setFont(SEM, 8.6); c.setFillColor(PLUM)
c.drawString(ML, y, "Configuration keys")
y -= 6
c.setFont(REG, 7.4); c.setFillColor(PLUM_40)
c.drawString(ML + 118, y + 0.6, "names only — no values appear in this document")
y -= 16

envs = ["ASPNETCORE_ENVIRONMENT", "DATABASE_URL", "UPLOADS_PATH",
        "OpenAI__ApiKey", "OpenAI__BaseUrl", "Admin__Email",
        "Demo__Email", "Demo__Password", "Demo__AutoReset",
        "Email__From", "Email__BaseUrl", "Resend__ApiKey",
        "Google__ClientId", "Google__ClientSecret",
        "RateLimiting__AI__Practice__PermitLimit"]
colw = CW / 3
for i, e in enumerate(envs):
    cx2 = ML + (i % 3) * colw
    cy2 = y - (i // 3) * 15
    c.setFillColor(BLUE); c.circle(cx2 + 2.6, cy2 + 2.8, 2.2, fill=1, stroke=0)
    c.setFont(REG, 7.4); c.setFillColor(PLUM_60)
    c.drawString(cx2 + 11, cy2, e)
y -= (len(envs) // 3 + 1) * 15 + 12

rbox(c, ML, y - 46, CW, 46, fill=CREAM_50, stroke=None, r=10)
para(c, "No key, connection string, token or real user record appears anywhere "
        "in this document. Local development reads .NET user-secrets; production "
        "reads environment variables set on the platform.",
     ML + 16, y - 20, CW - 32, size=8.2, lead=11.6, color=PLUM_60)

d.save()
print("built:", OUT)
