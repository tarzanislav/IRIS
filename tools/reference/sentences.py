"""Invented English test sentences for checking the IRIS core against the reference (CC0, written for IRIS; names,
titles and events are made up). The same seed always gives the same sentences.
"""
import random

FIXED = [
    "The premiere is tonight.",
    "Thanks!",
    "Yes.",
    "Where is the nearest cinema?",
    "The film opens in theaters on Friday, March 12.",
    "Critics called the ending brave, but the audience was divided.",
    "BREAKING NEWS: the studio has cancelled the second season.",
    "It's not the director's best work, but it's still worth seeing.",
    "Don't miss the new trailer, which was released this morning.",
    "The budget grew from $40 million to $65.5 million during production.",
    "About 1,200 people attended the screening in the old town square.",
    "The series has a 92% approval rating among critics.",
    "Filming took place in Vienna, Prague and Bratislava over 14 weeks.",
    "The actress, who was born in 1987, started her career on stage.",
    "Who will play the villain in the sequel?",
    "\"I wanted to make a film about silence,\" the director explained.",
    "The story follows a young pilot (played by a newcomer) who loses her memory.",
    "Episodes will be released weekly - every Tuesday at 9 p.m.",
    "The documentary is 2 hours and 17 minutes long.",
    "My brother doesn't like horror films, so we watched a comedy instead.",
    "Please turn off your phones before the show starts.",
    "The award ceremony was postponed because of the storm.",
    "Zoë and José met on the set of a short film in 2019.",
    "Tickets cost 9.50 euros, or 7 euros for students.",
    "The novel was adapted into a mini-series with six episodes.",
    "Is the sound better in the large hall or in the small one?",
    "The cast includes three Oscar winners and two debutants.",
    "Production was halted after the lead actor was injured during a stunt.",
    "The film was shot on 35 mm film and later restored in 4K.",
    "We are sorry, but tonight's screening is sold out.",
    "The soundtrack was composed by a young musician from Lisbon.",
    "Her performance was quiet, precise and deeply moving.",
    "The plot is simple: two strangers share a taxi and cannot stop talking.",
    "After ten years, the band reunited for one last concert.",
    "The museum is open from 10 a.m. to 6 p.m., except on Mondays.",
    "Can you recommend a good film for a rainy evening?",
    "The writers' strike delayed the production of many shows.",
    "The first episode was watched by 3.4 million viewers.",
    "In the final scene, the camera slowly rises above the city.",
    "This is the longest film I have ever seen in a cinema.",
    "The festival jury awarded the main prize to a debut feature.",
    "Subtitles are available in English, Slovak and Czech.",
    "Although the effects are impressive, the characters feel thin and the dialogue is often clumsy, "
    "which makes the second half of the film drag.",
    "The studio confirmed on Monday that the spin-off, which had been in development since 2021, "
    "will start filming in the spring with most of the original cast returning.",
    "He said the script had been rewritten at least seven times before the first day of shooting.",
    "We watched it twice 🎬 and loved it.",
    "The café on the corner shows old films every Sunday afternoon.",
    "What time does the last train leave?",
    "The weather was perfect for an open-air screening.",
    "Children under 12 must be accompanied by an adult.",
    "The heroine finally returns to the village where she grew up.",
    "The meeting was moved to Thursday at 3:30.",
    "Please send me the schedule for next week.",
    "The new edition contains deleted scenes and a commentary by the editor.",
    "Nobody expected the small film to become a worldwide hit.",
    "The sequel will be released in December, just before Christmas.",
    "The library will be closed for renovation until the end of May.",
    "She has written five books and two screenplays.",
    "The city plans to build a new concert hall near the river.",
    "If it rains tomorrow, the screening will move indoors.",
]

NAMES = ["Mara Velin", "Tomas Brek", "Elena Sorr", "Jonas Hale", "Ivy Marlow", "Peter Lunt", "Nora Quill", "Adam Ferro",
         "Lina Dask", "Victor Amell", "Sara Kovalen", "Oliver Strand", "Dana Morrow", "Felix Arden", "Rita Calloway"]
TITLES = ["The Glass Orchard", "Night Over Kestrel", "Paper Lanterns", "The Last Ferry", "Northern Signal", "Salt and Iron",
          "A House of Echoes", "The Quiet Harbor", "Silver Meridian", "The Winter Courier", "Blue Hour", "The Hollow Crown Road"]
SERIES = ["Harbor Lights", "The Night Desk", "Kestrel Bay", "Signal Point", "The Archivists", "Common Ground",
          "Frontier Clinic", "The Long Summer"]
PLACES = ["Vienna", "Prague", "Bratislava", "Lisbon", "Berlin", "Krakow", "Budapest", "a small coastal town",
          "a mountain village", "an abandoned factory", "a busy hospital", "a remote island"]
GENRES = ["thriller", "comedy", "drama", "science fiction story", "family adventure", "crime drama", "musical",
          "war film", "romantic comedy", "documentary"]
ROLES = ["detective", "young doctor", "retired pilot", "journalist", "teacher", "lawyer", "farmer", "police officer",
         "museum guard", "radio host"]
WEEKDAYS = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"]
MONTHS = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October",
          "November", "December"]
ADJECTIVES = ["beautiful", "dark", "slow", "funny", "strange", "honest", "ambitious", "quiet", "brilliant", "uneven",
              "surprising", "gentle"]
NOUNS = ["story", "music", "camera work", "script", "ending", "cast", "editing", "dialogue", "pacing", "first act"]
ITEMS = ["umbrella", "phone", "keys", "jacket", "notebook", "bag", "glasses", "ticket"]
PLACE_TYPES = ["station", "office", "cinema", "library", "restaurant", "hotel", "airport", "park"]
JOBS = ["editor", "cameraman", "sound engineer", "costume designer", "producer", "screenwriter", "actor", "critic"]
QUOTES = ["We made this film for our parents", "Every scene was a small battle", "I never thought it would end like this",
          "The story found us, not the other way round", "We had very little money but a lot of time",
          "Comedy is harder than drama"]
WEATHER = ["rain", "heat", "fog", "snow", "wind", "cold weather"]


def _templates(r: random.Random):
    name, name2 = r.sample(NAMES, 2)
    title = r.choice(TITLES)
    series = r.choice(SERIES)
    place = r.choice(PLACES)
    n = r.randint(2, 12)
    noun, noun2 = r.sample(NOUNS, 2)
    adj, adj2 = r.sample(ADJECTIVES, 2)
    day = r.choice(WEEKDAYS)
    date = f"{r.choice(MONTHS)} {r.randint(1, 28)}"
    return [
        f"{name} will direct {title}, a {r.choice(GENRES)} set in {place}.",
        f"The {adj} {noun} of {title} is the best thing about it.",
        f"Critics praised the {noun} but found the {noun2} too {adj2}.",
        f"{title} earned {r.randint(3, 180)} million dollars in its first weekend.",
        f"Season {n} of {series} will have {r.randint(6, 13)} episodes.",
        f"{name} joins the cast of {series} as a {r.choice(ROLES)}.",
        f"The festival in {r.choice(PLACES[:7])} opens on {date} with {title}.",
        f"Is {title} worth watching on a {adj} evening?",
        f"\"{r.choice(QUOTES)},\" said {name} at the premiere.",
        f"The trailer shows {name} running through {place} at night.",
        f"I left my {r.choice(ITEMS)} at the {r.choice(PLACE_TYPES)} yesterday.",
        f"Can you send me the {r.choice(['report', 'tickets', 'photos', 'contract', 'list'])} before {r.randint(1, 11)} o'clock?",
        f"The {r.choice(WEATHER)} will continue until {day} morning.",
        f"We need {n} more chairs for the meeting on {day}.",
        f"{name} has worked as a {r.choice(JOBS)} for {n} years.",
        f"{name} and {name2} will star in {title}, which opens on {date}.",
        f"The new season of {series} begins on {day}, and the first two episodes will be shown together.",
        f"After the screening in {r.choice(PLACES[:7])}, {name} answered questions from the audience for almost an hour.",
        f"{title} is a {adj} {r.choice(GENRES)} about a {r.choice(ROLES)} who returns to {place}.",
        f"Why did the {r.choice(ROLES)} leave {place} in the first episode?",
    ]


def sentences(count: int = 500, seed: int = 2026) -> list:
    r = random.Random(seed)
    result = list(FIXED)
    seen = set(result)
    while len(result) < count:
        sentence = r.choice(_templates(r))
        if sentence not in seen:
            seen.add(sentence)
            result.append(sentence)
    return result[:count]


if __name__ == "__main__":
    for s in sentences():
        print(s)
